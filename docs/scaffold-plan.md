# Scaffold plan

This document tells an agent how to generate the first skeleton. The product description and the decisions are in `docs/product-plan.md`. Read that file first. Do not add product behavior that it does not contain.

The person who owns the Azure subscription will connect Foundry, Blob, Content Understanding, and Entra later. The skeleton must run without those connections. Do not provision Azure resources. Do not put secrets in the repository.

The backend is two microservices: the API and the worker. The website is a separate application. The extension is a client of the API, not a service. Do not add further services.

## What to create

One git repository, three runnable programs, one extension.

```text
web/                 Next.js, React. Admin and user screens. No chat.
api/                 ASP.NET Core. Website and extension call this.
worker/              ASP.NET Core. Consumes one RabbitMQ queue.
extension/           Chromium extension, Manifest V3. Chat lives here.
infra/docker-compose.yml
```

Target framework: `net11.0`.

Web: current stable Next.js with the App Router. TypeScript. English text.

Extension: plain Manifest V3, no extension framework. Chrome and Edge. A side panel. No admin UI inside the extension. Filling is specified in `docs/form-model-specification.md`.

## Local infrastructure

`infra/docker-compose.yml` runs only what the skeleton needs before outside accounts exist:

- RabbitMQ with the management UI.
- PostgreSQL, for the API, until a Neon connection string is set.

Neon remains the chosen hosted database. The container is a stand-in. Local `dotnet run` reads `appsettings.Development.json`. Compose sets `DATABASE_URL` and `RABBITMQ_URL` for the containers. Do not load a `.env` file from C#.

Do not add Qdrant, Blob, Foundry, or Entra containers. Those are configured by URL and key later.

RabbitMQ queues: `file-uploaded`, where one message means a file was stored and the worker should process it, and `form-interpretation-requested`, where one message means the admin started an interpretation. No other queues.

## Configuration

`.env.example` at the repository root lists the variable names. Compose may load `.env`. The API and the worker read configuration through the ASP.NET host: `appsettings.json`, `appsettings.Development.json`, then environment variables. Empty secret values mean "not connected". The process still starts.

| Variable | Used by | When empty |
|---|---|---|
| `DATABASE_URL` | API | Required. Default in the example is the local Postgres container. |
| `RABBITMQ_URL` | API, worker | Required. Default is the local RabbitMQ container. |
| `BLOB_CONNECTION_STRING` | API, worker | File bytes are not stored in Azure. The API keeps the uploaded bytes on local disk under a gitignored folder and records that path. |
| `FOUNDRY_ENDPOINT` | API, worker | Chat and embeddings return a clear "not connected" result. |
| `FOUNDRY_API_KEY` | API, worker | Same as above. |
| `FOUNDRY_CHAT_DEPLOYMENT` | API | Default `claude-haiku-5-5`. |
| `FOUNDRY_EMBEDDING_DEPLOYMENT` | worker | Empty. The embeddings model was not chosen. |
| `CONTENT_UNDERSTANDING_ENDPOINT` | worker | Worker records the file as stored and does not call Content Understanding. |
| `CONTENT_UNDERSTANDING_API_KEY` | worker | Same as above. |
| `QDRANT_URL` | API, worker | Retrieval returns no fragments. The worker does not write vectors. |
| `QDRANT_API_KEY` | API, worker | Same as above. |
| `PLAYWRIGHT_MCP_URL` | worker | An interpretation run is marked not connected. |
| `PLAYWRIGHT_MCP_API_KEY` | worker | Same as above. |
| `FOUNDRY_AGENT_DEPLOYMENT` | worker | Same as above. The model of the agent is decided by the first test. |
| `FOUNDRY_MODEL_DEPLOYMENT` | worker | Default `claude-sonnet-5-5`. Builds the form model. |
| `ENTRA_TENANT_ID` | API | See auth below. |
| `ENTRA_API_CLIENT_ID` | API | See auth below. |
| `DEVELOPMENT_AUTH` | API | `true` only in the example file. |

No secret values in the example file. Add the gitignored data folder and `.env` to `.gitignore`.

## Auth

The chosen login is Microsoft Entra External ID, because the users are customers of the product.

When `ENTRA_TENANT_ID` and `ENTRA_API_CLIENT_ID` are empty and `DEVELOPMENT_AUTH=true`, the API accepts a development header that selects a seeded admin or a seeded user. The header must be ignored when `DEVELOPMENT_AUTH` is not `true`.

Do not build a password store. Do not build a sign-up form. The website shows a development switch between the seeded admin and the seeded user, and the comment in the layout says it is removed when Entra is connected.

Seed two organizations, one admin, and one user who belongs to both. A user can belong to many organizations.

## Data the API stores

Only these records.

**Organization.** A name. Admins manage organizations. Nothing else was specified, so the only column besides the id is the name.

**User.** Email, role (`admin` or `user`), membership in one or more organizations, and an optional Content Understanding switch. Email is the identifier because External ID customers sign in with an email address. Do not add a profile, avatar, or extra role.

**File.** Organization, uploading user, original file name, storage location (Blob URL later, local path until then), time, a status (`stored`, `processing`, `ready`, `failed`), and an optional Content Understanding switch for this import. The user can upload and delete. Delete removes the metadata and the stored bytes. There is no admin upload and no admin delete. The admin can set the switch.

**Filled-form info.** Organization, user, time, and a text summary. Create and read only. No update and no delete, in the API or the UI, ever. The summary is information about what was filled from the stored documents. The values themselves stay on the target website. The extension does not open that page.

**Settings mismatch.** Organization, user, time, and a text saying what does not match and what information is required. The user creates it. The admin reads it. The admin updates the organization's settings. The concrete setting fields were not listed, so the settings record is one text value the admin can replace. Do not invent fields. Content Understanding is not part of that text. It has its own switches.

**Content Understanding.** Three on/off values, set by the admin: one global value for everyone, one optional value on a user, one optional value on an import. Which value wins when they disagree was not decided. Store all three. Do not invent the combination.

**Forms and models.** The form, its interpretation runs, model versions, choice lists, and publications, as specified in `docs/form-model-specification.md`.

## API surface

JSON. The development auth header selects the caller.

Admin, caller must be an admin:

- List and create organizations.
- List and create users. Creating a user requires an email, the role, and at least one organization id. A user can be added to further organizations.
- List settings-mismatch reports for organizations.
- Replace the settings text for an organization.
- Set Content Understanding on or off for everyone, for one user, or for one import. The admin does not upload or delete files.

User, caller must be a user:

- List, upload, and delete files for an organization the caller belongs to.
- List filled-form info for the caller. Read only.

Extension, caller must be a user:

- `POST /chat` with a message string only. No file ids. Document grounding is Foundry's responsibility, not the caller's. If Foundry is not configured, respond with a fixed English sentence that the model is not connected. Do not call the model until both Foundry variables are set. When they are set, call deployment `claude-haiku-5-5`. Do not open or modify a website from this endpoint.
- `POST /settings-mismatches` with a text. This reports that something does not match the settings and what information is required. The admin reads it and updates the settings.
- `POST /filled-forms` creates one read-only info record from a text summary. This is how a later fill can leave something for the website to show. Do not accept edits.

Uploading a file saves the bytes, inserts the row with status `stored`, and publishes one `file-uploaded` message with the file id.

## Worker

The worker consumes `file-uploaded` and `form-interpretation-requested`.

For each `file-uploaded` message:

1. Set the file status to `processing`.
2. If Content Understanding is not configured, or `IsContentUnderstandingEnabled` is false, set the status back to `stored` and finish. Do not add another reader. Which switch wins is an open question, so that method returns only the global value until the combination is decided.
3. If the call is enabled and configured, send the file and keep the returned text.
4. If the embeddings deployment and Qdrant are configured, write the fragments to Qdrant with the file id and the organization id, then set status to `ready`.
5. If they are not configured, set status to `stored`.
6. On a thrown error, set status to `failed`.

Do not invent a chunk size, an embedding model, or a Qdrant collection schema beyond file id, organization id, and text. Put those three fields in one payload so it can be replaced later.

For each `form-interpretation-requested` message, the worker runs the interpretation specified in `docs/form-model-specification.md`. When the Playwright workspace or the Foundry values are empty, it marks the run as not connected and finishes.

## Web screens

English. No chat component.

Admin:

- Organization list and a form with one field, the name.
- User list and a form with email, role, and organization.
- Settings-mismatch list, and a text field that replaces that organization's settings. No extra setting fields.
- One control for Content Understanding for everyone, one on each user, and one on each import. No upload and no delete on the import list.
- Forms: a list and a form with the address and the description. A page per form with its runs, the live view link while a run waits for signing in, the report, the screenshots, the draft model with editing, the choice lists, and publishing to organizations.

User:

- File list, a file input, and a delete control on each row. Show the status.
- Filled-form list. Each row shows the time and the summary. No edit control and no delete control.
- Settings route that renders one sentence: settings have not been specified.

The development switch sits in the layout.

## Extension

Manifest V3. Permissions limited to a side panel and storage. Do not request access to websites. The extension does not open the page where a form is filled.

The side panel is a chat transcript and a text box. Sending calls `POST /chat` with the message only. The API base URL comes from the extension options. Default `http://localhost:5000`, or the port the API uses. Show the response. Do not send file ids. Do not write into any page. A control that is not a chat may replace this later. Do not build that control now.

## What the agent must not do

- Provision or modify Azure, Neon, Qdrant Cloud, CloudAMQP, or Entra.
- Invent the list of setting fields, form fields, or a file-picker contract.
- Treat the chat panel as the final interaction. It is the current assumption.
- Put a chat on the website.
- Add an admin screen for files or for filled forms.
- Add update or delete for filled-form info.
- Choose an embeddings model.
- Add a queue other than the two above, a fourth service, or a database other than PostgreSQL.
- Commit `.env` or uploaded files.
- Use the project name eisenberg. Do not invent a second name.

## Done when

- `docker compose` in `infra` starts Postgres and RabbitMQ.
- The API starts with the example env, the development switch works, and the five screens above render.
- Upload stores a file locally, writes a row, and publishes `file-uploaded`.
- The worker consumes that message and leaves the status at `stored` while Content Understanding, embeddings, and Qdrant are empty.
- Delete removes the row and the local bytes.
- The filled-form list has no edit or delete.
- Starting an interpretation without the Playwright workspace and Foundry values marks the run as not connected.
- The extension side panel sends a message and shows the "model is not connected" sentence.
- The solution builds.
