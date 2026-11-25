# Decisions that can change

These are the choices made where the product plan left a question open. Each one is the current behavior of the code. Any of them can be replaced later without treating the rest of the plan as wrong.

1. Foundry region is West Europe. The application does not create the resource. The endpoint URL is whatever the subscription owner sets.

2. After testing, the three containers stay the unit of deployment. Which cluster hosts them is still an operational choice, not a code choice.

3. Retrieval stays on Qdrant. Azure AI Search is not a second index.

4. A user lists and deletes only the files that user uploaded. A user cannot see or delete another user's file, even in a shared organization. Records are built from every reading in the organization, and every member of that organization can see those records.

5. The user's settings page shows the settings text of each organization the user belongs to. The user does not edit it. The admin replaces that text on the Organizations page. The mismatches page lists reports and can replace the same text. When something does not match, the user reports that to the admin.

6. The embeddings deployment name is `text-embedding-3-large` when `FOUNDRY_EMBEDDING_DEPLOYMENT` is empty. Embeddings still run only when the Foundry endpoint, the Foundry key, and Qdrant are set.

7. Content Understanding is on or off by the most specific switch that has a value: the import, otherwise the user, otherwise the global switch.

8. The interpretation pass is the worker loop in `AgentPass`, not a Foundry-hosted agent. The model that clicks and looks at screenshots is `claude-haiku-5-5` when `FOUNDRY_AGENT_DEPLOYMENT` is empty. The model that builds the form model is `claude-sonnet-5-5` when `FOUNDRY_MODEL_DEPLOYMENT` is empty. The pass still does not start when the Foundry endpoint, the Foundry key, or the Playwright workspace is missing.

9. One interpretation run stops at 400 actions, 60 minutes, or 60 options tried on one control. Those are the values of `INTERPRETATION_MAX_ACTIONS`, `INTERPRETATION_MAX_MINUTES`, and `INTERPRETATION_MAX_SWEEP_OPTIONS` when the variables are empty.

10. A user is created with one or more organizations. Further organizations are added from that user's row. Membership is not removed in this version.

11. The website is two panels. Admin covers organizations, users, forms, reports, and document reading. User covers documents, records, filled forms, and settings. The extension is the fill tool for a saved record.

12. A form-change report is separate from a settings report. It names the published form whose open page no longer matches the model. The extension creates it. The admin starts a new interpretation from that report, and the report is then marked handled. A settings report stays about the organization settings text. The user sends that from the settings page.

13. A filled-form record stores the form address and the record label, plus the summary. It does not store the values that were entered. Those stay on the website that was filled.

14. A file stores a status sentence: not connected, reading off, read but not indexed, read and indexed, or the failure text. The user who uploaded the file can open the reading, with the text, the page, the place on the page, and the records that cite the file. The admin sees whether Content Understanding is connected and each file's status sentence. The admin does not upload or delete files.

15. A generated record stores, for each value the reading can locate, the document name, the page, and the place. Changing a value drops that value's source. A rejected save returns one problem per property, and the record page shows that problem on the property.

16. The extension uses the account from the website. A script on the website copies the sign-in token and the development role into the extension. Calls send that token when it exists, and the development role otherwise. The extension does not keep a separate account.

17. A record added by hand uses a type from the published model. The page shows that type's fields. The save is checked against those fields.

18. Replacing an organization's settings marks that organization's open settings reports as handled. They leave the admin overview. The reports page still lists them, marked as settings replaced.

19. When Content Understanding is not connected and reading is not switched off, a plain-text upload is stored as a reading with one page. PDF and other binary files stay stored, with the sentence that Content Understanding is not connected. This is not a second cloud reader.

20. When Foundry is not connected and a reading exists, records are built locally from the published model: a complete choice is taken when its label or key appears in the reading, and a text, number, or date is taken from a line that starts with the page label or the property name. A record that fails validation is dropped. When Foundry is connected, Claude Haiku builds the records instead. The same fill function the extension uses is checked by `tools/rehearse/fill.mjs`.

21. The website does not call localhost. The browser calls the website, and the website container calls `API_BASE_URL`. `PUBLIC_API_URL` is the API address a browser and the extension use. `WEB_ORIGIN` is allowed by the API in addition to localhost and the extension. Local Compose keeps development login. `infra/docker-compose.prod.yml` and `infra/k8s/eisenberg.yaml` run the three containers with development login off. Entra and the other service addresses still come from the environment. The application does not create those services.

22. `tools/py` holds three offline Python scripts: draw the places in a reading onto the document pages, score records against an expected pack, and export corrected records as a Foundry training line. Scoring without Foundry runs `tools/Eisenberg.Pack`, which calls the same `LocalRecords` code as the worker. The scripts are not a service and they are not on the path from an upload to a filled page.
