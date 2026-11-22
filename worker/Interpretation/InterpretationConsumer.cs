using System.Text;
using Eisenberg.Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Eisenberg.Worker.Interpretation;

sealed class InterpretationConsumer(IServiceProvider services, IConfiguration configuration, Interpreter interpreter, ILogger<InterpretationConsumer> logger) : BackgroundService
{
    private const string Queue = "form-interpretation-requested";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedRunsAsync(stoppingToken);
        var url = configuration["RABBITMQ_URL"] ?? throw new InvalidOperationException("RABBITMQ_URL is required.");
        var factory = new ConnectionFactory { Uri = new Uri(url), DispatchConsumersAsync = true };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.QueueDeclare(Queue, durable: true, exclusive: false, autoDelete: false);
        channel.BasicQos(0, 1, false);
        await RequeueQueuedRunsAsync(channel, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, delivery) =>
        {
            try
            {
                if (!Guid.TryParse(Encoding.UTF8.GetString(delivery.Body.Span), out var runId))
                {
                    channel.BasicAck(delivery.DeliveryTag, false);
                    return;
                }

                // Claim the run in the database before acknowledging so a crash after ack
                // cannot leave the form stuck in queued with no message left on the broker.
                if (!await ClaimAsync(runId, stoppingToken))
                {
                    channel.BasicAck(delivery.DeliveryTag, false);
                    return;
                }

                channel.BasicAck(delivery.DeliveryTag, false);
                await interpreter.RunAsync(runId, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "form-interpretation-requested failed");
                try { channel.BasicAck(delivery.DeliveryTag, false); } catch { /* channel may already be closed */ }
            }
        };
        channel.BasicConsume(Queue, autoAck: false, consumer);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> ClaimAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        var run = await db.InterpretationRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.Status != RunStatus.Queued)
            return false;
        run.Status = RunStatus.Running;
        run.StatusText = "Claimed by the worker.";
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task RequeueQueuedRunsAsync(IModel channel, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        var queued = await db.InterpretationRuns
            .Where(r => r.Status == RunStatus.Queued)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in queued)
            channel.BasicPublish("", Queue, body: Encoding.UTF8.GetBytes(id.ToString()));
        if (queued.Count > 0)
            logger.LogInformation("Re-queued {Count} interpretation runs left in queued.", queued.Count);
    }

    private async Task FailInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        var interrupted = await db.InterpretationRuns
            .Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.WaitingForSignIn ||
                        r.Status == RunStatus.BuildingModel || r.Status == RunStatus.Checking)
            .ToListAsync(cancellationToken);
        foreach (var run in interrupted)
        {
            run.Status = RunStatus.Failed;
            run.StatusText = "The worker stopped during the run.";
            run.FinishedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
