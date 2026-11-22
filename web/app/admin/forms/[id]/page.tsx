"use client";

import { useParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { api, apiBlobUrl } from "../../../../lib/api";

type Organization = { id: string; name: string };
type RunRow = { id: string; status: string; statusText: string; createdAt: string; actionCount: number; stateCount: number };
type ModelRow = { id: string; number: number; status: string; runId: string; createdAt: string };
type FormDetail = {
  id: string;
  address: string;
  description: string;
  publishedModelId: string | null;
  organizationIds: string[];
  runs: RunRow[];
  models: ModelRow[];
};
type Check = { name: string; status: string; details: string[] };
type Usage = { role: string; deployment: string; calls: number; inputTokens: number; outputTokens: number } | null;
type Report = {
  stoppedBecause: string;
  agentSummary?: string;
  error?: string | null;
  missingSettings?: string[];
  summary?: { states: number; actions: number; controls: number; steps: number; minutes: number };
  models?: Usage[];
  checks?: Check[];
  problems?: string[];
  steps?: { index: number; url: string; headings: string[]; arrivedWith: string[] }[];
};
type Artifact = { id: string; sequence: number; kind: string; caption: string; contentType: string };
type RunDetail = {
  id: string;
  status: string;
  statusText: string;
  liveViewUrl: string | null;
  actionCount: number;
  stateCount: number;
  report: Report | null;
  modelId: string | null;
  artifacts: Artifact[];
};
type ControlInfo = { key: string; pageText: string; kind: string; step: number; ignored?: boolean; conditions: { text: string }[][] };
type Property = {
  name: string;
  description: string;
  kind: string;
  required: boolean;
  limits: string;
  shownWhen: string;
  ignored: boolean;
  controls: ControlInfo[];
  choiceListId: string | null;
};
type ModelType = { name: string; description: string; subclassOf: string | null; repeated: boolean; properties: Property[] };
type Model = { types: ModelType[]; unmapped: ControlInfo[] } & Record<string, unknown>;
type ChoiceListRow = {
  id: string;
  isComplete: boolean;
  dependsOn: { label: string }[];
  choices: { choices: { value: string; text: string }[] };
};
type ModelDetail = { id: string; number: number; status: string; model: Model; choiceLists: ChoiceListRow[] };

const activeStatuses = ["queued", "running", "waiting-for-sign-in", "building-model", "checking"];
const kinds = ["text", "number", "date", "choice", "multi-choice", "boolean", "file"];

export default function FormPage() {
  const { id } = useParams<{ id: string }>();
  const [form, setForm] = useState<FormDetail | null>(null);
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [description, setDescription] = useState("");
  const [runId, setRunId] = useState<string | null>(null);
  const [run, setRun] = useState<RunDetail | null>(null);
  const [model, setModel] = useState<ModelDetail | null>(null);
  const [draft, setDraft] = useState<Model | null>(null);
  const [publishTo, setPublishTo] = useState<string[]>([]);
  const [shots, setShots] = useState<Record<string, string>>({});
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const loadForm = useCallback(async () => {
    const next = await api<FormDetail>(`/admin/forms/${id}`);
    setForm(next);
    setDescription((current) => current || next.description);
    setPublishTo((current) => current.length ? current : next.organizationIds);
    setRunId((current) => current ?? next.runs[0]?.id ?? null);
  }, [id]);

  useEffect(() => {
    loadForm().catch((e: Error) => setError(e.message));
    api<Organization[]>("/organizations").then(setOrganizations).catch((e: Error) => setError(e.message));
  }, [loadForm]);

  useEffect(() => {
    if (!runId) return;
    let stopped = false;
    async function poll() {
      const next = await api<RunDetail>(`/admin/runs/${runId}`);
      if (stopped) return;
      setRun(next);
      if (activeStatuses.includes(next.status)) setTimeout(() => poll().catch((e: Error) => setError(e.message)), 3000);
      else loadForm().catch((e: Error) => setError(e.message));
    }
    poll().catch((e: Error) => setError(e.message));
    return () => { stopped = true; };
  }, [runId, loadForm]);

  useEffect(() => {
    if (!run?.modelId) { setModel(null); setDraft(null); return; }
    api<ModelDetail>(`/admin/models/${run.modelId}`).then((next) => {
      setModel(next);
      setDraft(structuredClone(next.model));
    }).catch((e: Error) => setError(e.message));
  }, [run?.modelId]);

  useEffect(() => {
    if (!run) return;
    for (const artifact of run.artifacts.filter((item) => item.kind === "screenshot").slice(0, 60)) {
      if (shots[artifact.id]) continue;
      apiBlobUrl(`/admin/artifacts/${artifact.id}`)
        .then((url) => setShots((current) => ({ ...current, [artifact.id]: url })))
        .catch(() => undefined);
    }
  }, [run, shots]);

  async function act(work: () => Promise<void>, done: string) {
    setError("");
    setNotice("");
    try {
      await work();
      setNotice(done);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const saveDescription = () => act(async () => {
    await api(`/admin/forms/${id}`, { method: "PUT", body: JSON.stringify({ description }) });
    await loadForm();
  }, "Description saved.");

  const startRun = () => act(async () => {
    await api(`/admin/forms/${id}`, { method: "PUT", body: JSON.stringify({ description }) });
    const created = await api<{ runId: string }>(`/admin/forms/${id}/runs`, { method: "POST" });
    setRun(null);
    setRunId(created.runId);
    await loadForm();
  }, "Run started.");

  const signedIn = () => act(async () => {
    await api(`/admin/runs/${runId}/signed-in`, { method: "POST" });
  }, "The run continues.");

  const saveModel = () => act(async () => {
    await api(`/admin/models/${model!.id}`, { method: "PUT", body: JSON.stringify({ model: draft }) });
  }, "Model saved.");

  const publish = () => act(async () => {
    await api(`/admin/models/${model!.id}`, { method: "PUT", body: JSON.stringify({ model: draft }) }).catch(() => undefined);
    await api(`/admin/models/${model!.id}/publish`, { method: "POST", body: JSON.stringify({ organizationIds: publishTo }) });
    setModel({ ...model!, status: "published" });
    await loadForm();
  }, "Model published.");

  async function openArtifact(artifact: Artifact) {
    const url = await apiBlobUrl(`/admin/artifacts/${artifact.id}`);
    window.open(url, "_blank");
  }

  function editProperty(typeIndex: number, propertyIndex: number, change: Partial<Property>) {
    setDraft((current) => {
      if (!current) return current;
      const next = structuredClone(current);
      Object.assign(next.types[typeIndex].properties[propertyIndex], change);
      return next;
    });
  }

  function editType(typeIndex: number, change: Partial<ModelType>) {
    setDraft((current) => {
      if (!current) return current;
      const next = structuredClone(current);
      Object.assign(next.types[typeIndex], change);
      return next;
    });
  }

  function ignoreUnmapped(index: number, ignored: boolean) {
    setDraft((current) => {
      if (!current) return current;
      const next = structuredClone(current);
      next.unmapped[index].ignored = ignored;
      return next;
    });
  }

  const lists = new Map((model?.choiceLists ?? []).map((list) => [list.id, list]));
  const editable = model?.status === "draft";
  const report = run?.report;

  if (!form) return <main>{error || "Loading."}</main>;

  return (
    <main className="wide">
      <h1>{form.address}</h1>
      {error && <p className="failed">{error}</p>}
      {notice && <p className="note">{notice}</p>}

      <section>
        <h2>Description</h2>
        <form className="stack" onSubmit={(event) => { event.preventDefault(); saveDescription(); }}>
          <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={5} />
          <div className="row">
            <button type="submit">Save description</button>
            <button type="button" onClick={startRun} disabled={!!run && activeStatuses.includes(run.status)}>Start a new run</button>
          </div>
        </form>
      </section>

      <section>
        <h2>Runs</h2>
        <table>
          <thead><tr><th>Started</th><th>Status</th><th>Actions</th><th>States</th><th /></tr></thead>
          <tbody>
            {form.runs.map((row) => (
              <tr key={row.id}>
                <td>{new Date(row.createdAt).toLocaleString()}</td>
                <td>{row.status}</td>
                <td>{row.actionCount}</td>
                <td>{row.stateCount}</td>
                <td>{row.id === runId ? "shown" : <button type="button" onClick={() => setRunId(row.id)}>Show</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {run && (
        <section>
          <h2>Run</h2>
          <p><strong>{run.status}</strong>: {run.statusText}</p>
          <p className="note">{run.actionCount} actions, {run.stateCount} page states recorded.</p>
          {run.liveViewUrl && (
            <div className="row">
              <a href={run.liveViewUrl} target="_blank" rel="noreferrer">Open the live view</a>
              {run.status === "waiting-for-sign-in" && <button type="button" onClick={signedIn}>I have signed in</button>}
            </div>
          )}
        </section>
      )}

      {report && (
        <section>
          <h2>Report</h2>
          {report.missingSettings && <p>Empty settings: {report.missingSettings.join(", ")}.</p>}
          {report.summary && (
            <p className="note">
              {report.summary.controls} controls, {report.summary.steps} steps, {report.summary.states} states,
              {" "}{report.summary.actions} actions, {report.summary.minutes} minutes. Stopped because: {report.stoppedBecause}.
            </p>
          )}
          {report.agentSummary && <p>{report.agentSummary}</p>}
          {report.error && <p className="failed">{report.error}</p>}
          {report.checks && (
            <table>
              <thead><tr><th>Check</th><th>Status</th><th>Details</th></tr></thead>
              <tbody>
                {report.checks.map((check) => (
                  <tr key={check.name}>
                    <td>{check.name}</td>
                    <td className={check.status}>{check.status}</td>
                    <td>{check.details.length ? <ul>{check.details.slice(0, 30).map((detail) => <li key={detail}>{detail}</li>)}</ul> : null}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {!!report.problems?.length && (
            <>
              <h3>Problems</h3>
              <ul>{report.problems.map((problem) => <li key={problem}>{problem}</li>)}</ul>
            </>
          )}
          {!!report.steps?.length && (
            <>
              <h3>Steps</h3>
              <ol>
                {report.steps.map((step) => (
                  <li key={step.index}>
                    {step.headings[0] || step.url}
                    {step.arrivedWith.length > 0 && <span className="note"> reached with: {step.arrivedWith.slice(0, 8).join("; ")}</span>}
                  </li>
                ))}
              </ol>
            </>
          )}
          {!!report.models?.length && (
            <p className="note">
              {report.models.filter(Boolean).map((usage) => `${usage!.role}: ${usage!.deployment}, ${usage!.calls} calls, ${usage!.inputTokens} input and ${usage!.outputTokens} output tokens`).join(". ")}
            </p>
          )}
        </section>
      )}

      {model && draft && (
        <section>
          <h2>Model {model.number} ({model.status})</h2>
          {!editable && <p className="note">A published model is not edited. Start a new run for changes.</p>}
          {draft.types.map((type, typeIndex) => (
            <div key={typeIndex}>
              <h3>
                {editable ? <input type="text" value={type.name} onChange={(e) => editType(typeIndex, { name: e.target.value })} /> : type.name}
                {type.subclassOf && <span className="note"> subclass of {type.subclassOf}</span>}
                {type.repeated && <span className="note"> repeated</span>}
              </h3>
              <p className="note">{type.description}</p>
              <table>
                <thead><tr><th>Name</th><th>Description</th><th>Kind</th><th>On the page</th><th>Choices</th><th>Ignored</th></tr></thead>
                <tbody>
                  {type.properties.map((property, propertyIndex) => {
                    const list = property.choiceListId ? lists.get(property.choiceListId) : undefined;
                    return (
                      <tr key={propertyIndex}>
                        <td>{editable ? <input type="text" value={property.name} onChange={(e) => editProperty(typeIndex, propertyIndex, { name: e.target.value })} /> : property.name}</td>
                        <td>{editable ? <textarea rows={3} value={property.description} onChange={(e) => editProperty(typeIndex, propertyIndex, { description: e.target.value })} /> : property.description}</td>
                        <td>
                          {editable ? (
                            <select value={property.kind} onChange={(e) => editProperty(typeIndex, propertyIndex, { kind: e.target.value })}>
                              {kinds.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
                            </select>
                          ) : property.kind}
                          {property.required && <div className="note">required</div>}
                        </td>
                        <td>
                          {property.controls.map((control) => <div key={control.key}>{control.pageText}</div>)}
                          {property.shownWhen && <div className="note">shown when {property.shownWhen}</div>}
                        </td>
                        <td>
                          {list ? (
                            <details>
                              <summary>{list.choices.choices.length} {list.isComplete ? "complete" : "incomplete"}{list.dependsOn.length > 0 && `, depends on ${list.dependsOn.map((item) => item.label).join(", ")}`}</summary>
                              <ul>{list.choices.choices.slice(0, 200).map((choice) => <li key={choice.value}>{choice.text}</li>)}</ul>
                            </details>
                          ) : null}
                        </td>
                        <td><input type="checkbox" checked={property.ignored} disabled={!editable} onChange={(e) => editProperty(typeIndex, propertyIndex, { ignored: e.target.checked })} /></td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ))}

          {draft.unmapped.length > 0 && (
            <>
              <h3>Controls in no property</h3>
              <table>
                <thead><tr><th>On the page</th><th>Kind</th><th>Step</th><th>Ignored</th></tr></thead>
                <tbody>
                  {draft.unmapped.map((control, index) => (
                    <tr key={control.key}>
                      <td>{control.pageText}</td>
                      <td>{control.kind}</td>
                      <td>{control.step}</td>
                      <td><input type="checkbox" checked={!!control.ignored} disabled={!editable} onChange={(e) => ignoreUnmapped(index, e.target.checked)} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}

          <div className="row">
            {editable && <button type="button" onClick={saveModel}>Save model</button>}
            {organizations.map((organization) => (
              <label key={organization.id}>
                <input
                  type="checkbox"
                  checked={publishTo.includes(organization.id)}
                  onChange={() => setPublishTo((current) => current.includes(organization.id) ? current.filter((item) => item !== organization.id) : [...current, organization.id])}
                />
                {organization.name}
              </label>
            ))}
            <button type="button" onClick={publish} disabled={publishTo.length === 0}>Publish</button>
          </div>
        </section>
      )}

      {run && run.artifacts.length > 0 && (
        <section>
          <h2>Screenshots</h2>
          <div className="shots">
            {run.artifacts.filter((artifact) => artifact.kind === "screenshot").map((artifact) => (
              <figure key={artifact.id}>
                {shots[artifact.id] ? <img src={shots[artifact.id]} alt={artifact.caption} /> : null}
                <figcaption>{artifact.caption}</figcaption>
              </figure>
            ))}
          </div>
          <div className="row">
            {run.artifacts.filter((artifact) => artifact.kind === "actions").map((artifact) => (
              <button key={artifact.id} type="button" onClick={() => openArtifact(artifact)}>Recorded actions</button>
            ))}
          </div>
        </section>
      )}
    </main>
  );
}
