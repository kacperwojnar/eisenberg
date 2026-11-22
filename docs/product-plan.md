# Product and technology plan

Product documentation and the technical plan for building the application. The project is named eisenberg. Only decided behavior is described.

## What is being built

Two programs and a browser extension.

### Web application

- React and Next.js.
- English.
- Two roles: admin and user. An early note used the word "client" for the user. That word means user.
- No chat on the website.

Admin:

- Manages organizations.
- Manages users separately.
- An organization is a group of users.
- A user belongs to an organization and may belong to many. A user is not managed outside an organization.
- Adds forms with an address and a description, starts their interpretation, reviews and edits the model, and publishes it to organizations. This is specified in `docs/form-model-specification.md`.

User:

- Imports files.
- Deletes files. Upload and delete belong to the user, not the admin.
- Sees information about forms the extension has filled. That information cannot be changed or deleted here. The filled values stay on the target website and are stored in that website's database.
- Has settings. The individual settings were not listed.
- The flow checks whether things match those settings. When they do not, the missing information is reported to the admin. The admin updates the settings.

The website is where the user imports and deletes files. Those files are kept in Blob Storage. Azure Content Understanding, in standard mode, reads each file. The readings are indexed in Qdrant. Claude Haiku 5.5 builds the form instances from those readings. The form model, the instances, and filling are specified in `docs/form-model-specification.md`. Foundry is the external service the API and the worker call for that model and for embeddings. It is not part of the website.

### Browser extension

- Chromium extension, Chrome and Edge.
- Users only. Admins do not get it.
- It is not a second version of the user website.
- The interaction that fills a form lives only here. The user selects one instance. The extension enters that instance into the open tab through the Chrome DevTools Protocol. Computer use is used when a control cannot be addressed in the page structure.
- Filling uses the instances built from the documents. The extension does not send file ids.
- The filled values stay on the target website and are stored in that website's database. This application only shows information about what was filled.

It was not stated whether a user sees every file in an organization they belong to, or only the files they uploaded.

## Stack

| Layer | Choice |
|---|---|
| Web UI | React, Next.js |
| Login | Microsoft Entra External ID. Users are customers of the product. |
| Application code | .NET 11, containers, Docker. |
| Shape | Microservices. The API and the worker are separate services. The website is its own application. Kubernetes is the intended runtime. Where the containers run after testing is open. |
| Models | Claude Haiku 5.5 on Microsoft Foundry, deployment name `claude-haiku-5-5`, builds instances and serves the chat endpoint. Claude Sonnet 5.5 on Microsoft Foundry builds the form model. The model of the interpretation agent is decided by the first test. |
| Form interpretation | A Foundry agent with the Browser automation tool, backed by a Playwright workspace. |
| Reading files | Azure Content Understanding, standard mode. It reads each file before the readings are indexed. |
| File bytes | Azure Blob Storage |
| Retrieval index | Qdrant. The current variant is Qdrant Free. Foundry has no connector for it. Application code writes the vectors and reads them back. |
| Database | Neon, PostgreSQL |
| Queue | RabbitMQ. Locally in Docker. The .NET RabbitMQ client. |
| Embeddings | A Foundry embeddings deployment. The model was not named. |

Azure resources are connected by the owner of the subscription. The application is written against them. It does not create them.

## Architecture

Two microservices run on the server: `api` and `worker`. The website is a third container. It is not a microservice, because nothing calls it to do work. The extension is not a container and not a microservice. It runs in the user's browser and calls `api`. `data` is a library shared by the two microservices. It is not deployed on its own.

```text
extension  --HTTP-->  api  --publish-->  RabbitMQ  -->  worker
web        --HTTP-->  api                      |            |
                                      Postgres (Neon) <-----+
                                      Blob, Foundry, Qdrant, Content Understanding, Playwright workspace
```

Keys for Foundry, Blob, Qdrant, RabbitMQ, and the Playwright workspace stay in `api` and `worker`. They do not go in the browser or the extension.

### `web`

Next.js and React. Its own container.

Admin screens and user screens. English. No chat.

It calls `api` over HTTP. It does not talk to Blob, Foundry, Qdrant, RabbitMQ, or the database.

### `api`

.NET 11 microservice. Its own container. ASP.NET Core.

The only HTTP entry for the website and the extension.

Owns the request path for organizations, users, settings, file metadata, the read-only filled-form record, settings-mismatch reports, forms, interpretation runs, model publishing, and the chat endpoint. Only the extension calls the chat endpoint.

On upload it stores the file bytes (Blob when connected, local disk until then), writes the file row, and publishes one `file-uploaded` message. It does not read the document itself.

When the admin starts an interpretation, it writes the run and publishes one `form-interpretation-requested` message. It does not open the form's site itself.

Building instances does not go through a chat call. The worker calls Claude Haiku 5.5 with the model and the readings of the whole set of documents. The extension does not send file ids.

Login is Microsoft Entra External ID. Until that is connected, a development header selects the seeded admin or the seeded user. That header is ignored once Entra is configured.

### `worker`

.NET 11 microservice. Its own container. No public site.

Consumes two RabbitMQ queues, `file-uploaded` and `form-interpretation-requested`.

Reads the file from Blob. Calls Content Understanding in standard mode and writes the readings to Qdrant. Claude Haiku 5.5 builds the form instances from the readings of the whole set of documents.

For an interpretation, it runs the pass with the Browser automation tool, keeps the record of page states and actions, has Claude Sonnet 5.5 build the model, checks the result, and stores the draft model, the choice lists, the screenshots, and the report.

Does not serve the website or the extension.

### `extension`

Chromium extension for Chrome and Edge. Users only. Installed in the browser, not run on the server.

A client of `api`, the same way `web` is. The user selects one instance, and the extension enters it into the open tab through the Chrome DevTools Protocol.

It does not open the page where the form is filled. It does not hold service keys. It does not start an interpretation.

### `data`

.NET class library referenced by `api` and `worker`. PostgreSQL via EF Core. Organizations, users, memberships, files, filled-form info, settings text, settings mismatches, the Content Understanding switches, forms, interpretation runs, model versions, choice lists, and publications.

Not a service and not a container. Both microservices use this one schema so a file row written by `api` can be updated by `worker`.

### `infra`

`infra/docker-compose.yml` builds and runs the three containers, plus PostgreSQL as a stand-in until the Neon connection string is set, and RabbitMQ. `api` and `worker` share one volume for file bytes until Blob is connected. Foundry, Blob, Content Understanding, Qdrant, and Entra are not started here. They are connected later by URL and key.

Kubernetes is the intended place for the three containers (`web`, `api`, `worker`) after testing. Which cluster hosts them is open.

## Out of scope

- A chat on the website.
- An extension for admins.
- Editing or deleting filled-form records. This stays out of scope.
- The extension opening the page where the form is filled.
- Filling a form by a chat message. The extension enters the selected instance into the page.

## Open questions

1. Foundry region.
2. Where the containers run after testing.
3. Whether retrieval stays on Qdrant or moves to Azure AI Search.
4. Whether a user sees every file in an organization they belong to, or only the files they uploaded.
5. What the user's settings contain. The mismatch path is decided: a failed check is reported to the admin, and the admin updates the settings.
6. Which Foundry embeddings model.
7. Which Content Understanding switch wins when the global, user, and import values disagree.
8. Which model and which kind of Foundry agent perform the interpretation pass. The first test decides.
9. The budget of an interpretation run.
