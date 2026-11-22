# Form model module

Specification of what the system does. The admin has a form interpreted into a model and publishes it. The system builds instances of that model from the user's documents, and the extension fills the form from the instance the user selects.

## 1. Terms

**Form.** Any page a user fills in. The page structure is not known in advance, and forms are not limited to one site.

**Model.** The shape produced by interpreting a form. It defines the properties and holds no values of its own. It can be a programming model: types, nested types, and subclasses.

**Property.** One field of a model. It has a name and a description of the fact it holds. It is not the question text shown on the page. Where the form offers choices, the property refers to a choice list.

**Choice list.** The values the form offers for one property. Each choice has the value the form stores and the text the page shows. A choice list can depend on the values chosen for other properties. Choice lists are stored apart from the model. They are used when instances are built and when a form is filled.

**Instance.** One record of a model, of one of its types. It holds the values for that type's properties. A model has many instances. The values of one instance can come from different documents.

**Document.** A file the user has imported. Its content can be anything. There is no fixed list of document types.

**Report.** The result of checking an interpretation: what was checked, what could not be checked, and why.

## 2. Interpreting a form

The admin prepares the model of each form and publishes it. Users only work with published models.

### 2.1 Starting an interpretation

1. On the admin page, the admin adds a form with its address and a description.
2. The description says what the form is for, how to reach it after signing in, and what must not be clicked. It can include test values.
3. The interpretation runs in the background. The admin page shows its progress.

### 2.2 Signing in

1. When the site requires signing in, the run pauses and the admin page shows a link to the live view of the remote browser. The admin signs in there and returns control. The run continues in the same browser.
2. An idle session ends after 15 minutes and cannot be resumed. If the admin does not sign in within that time, the run ends.
3. The browser cannot reuse an earlier session. Every run on a site that requires signing in needs the admin at its start.
4. Credentials are not stored in this version.

### 2.3 The pass

1. A Foundry agent with the Browser automation tool performs the pass in a remote browser. The click loop is not code in this project.
2. The agent opens every control that hides content, tries every value of every control that changes the form, and moves through the steps of the form.
3. It does not click the final submit. Moving to the next step is allowed, even when the site saves the entered values.
4. The agent generates test values for required fields. When the site rejects a value and the agent finds no accepted one, the report names the field. The admin adds a value to the description and starts the run again.
5. After every action, a fixed script runs in the same browser. It lists every control on the page with its choices, and what appeared or disappeared since the previous action. This list does not depend on the accessibility snapshot.
6. The project records every page state and every action, and keeps a list of what has not been checked yet. The pass ends when nothing is left to check or the run reaches its budget. The agent does not decide on its own that the pass is complete.
7. Every action is recorded so it can be repeated. When the browser session ends, a new session repeats the recorded actions to return to the same state.
8. Choice lists are collected during the pass. For a list that depends on other properties, the run collects its choices for each combination of values of those properties, as long as the run stays within its budget. A list that exceeds the budget is stored as incomplete, together with the properties it depends on and the choices collected so far.

### 2.4 Building the model

1. Claude Sonnet 5.5 on Foundry builds the model from the recorded states and the admin's description. It names the properties, describes the facts, and groups the properties into types, subclasses, and repeated groups.
2. Only the page structure is sent to the model. Values already present in the form are not sent.
3. Each property records:
   - its name and the description of the fact;
   - the kind of value, and whether it is required;
   - its limits, such as format, range, or length;
   - its choice list;
   - the text shown on the page, and the data the extension uses to find the control;
   - the condition under which it is shown, and the properties that must be filled before it.
4. The model records the steps of the form and the structure of each step, so the extension can tell when the form has changed.

### 2.5 Checking the result

The run checks its own result and writes the report.

1. Every control seen in any state is a property, or the admin has marked it as ignored.
2. Every complete choice list holds exactly the choices the script listed.
3. Every value of every control that changes the form was tried. Every control it reveals has a recorded condition.
4. The pass reached the last step before the final submit, or the report says where it stopped and why.
5. Paths generated from the model are repeated in the browser. The controls and choices the page shows must match what the model predicts.
6. The form is filled once with a generated instance, the same way the extension fills it, and the values are read back. Every property must be found and set.

A gap that the report does not list is a defect of the interpretation.

### 2.6 Review and publishing

1. The admin sees the draft model, the choice lists, a screenshot of every state, and the report.
2. The admin can rename properties, change descriptions and types, mark controls as ignored, change the description, and start the run again.
3. The admin approves the model and publishes it to organizations. Only a published model is used to build instances and to fill forms.
4. A new run creates a new draft. The published model stays in use until the admin publishes another one.

### 2.7 When the form changes

The model is kept and reused. When the extension finds that the page no longer matches the stored structure, it reports the form to the admin, and the admin starts a new run. Filling a form does not itself start an interpretation.

### 2.8 Limits of the browser

The browser is remote. It does not see the user's session or any address that is not public. A snapshot is truncated at 64 KiB. A site that shows a CAPTCHA or blocks automated browsers stops the run, and the report says so. The browser has no proxy or fixed address, so a site that limits access by location can refuse it.

## 3. Documents and instances

What an instance contains depends on the documents. The model, including its subclasses, decides what is looked for. The data for one instance can be spread across the documents.

1. Azure Content Understanding, in standard mode, reads each file. The reading keeps the text, the page, and the location on the page. Pro mode is not used.
2. Those readings are indexed in Qdrant.
3. Claude Haiku 5.5 on Foundry, deployment `claude-haiku-5-5`, builds the instances. It receives the model, its choice lists, and the readings of the whole set of documents, not only the closest fragments from a search. It returns every instance that can be built, and it chooses the subclass for each one. For a property with a complete choice list, an instance holds one of the listed choices. For an incomplete list, it holds the value as the documents state it.
4. One model produces many instances.
5. Reading the files, indexing them, and creating instances can run in the background.
6. The user can see the instances on the user page.
7. The user can correct an instance and can extend it.

## 4. Extension

1. The form is filled in the browser extension, on the user's open tab.
2. The user selects one instance of the appropriate model. That instance already holds the values extracted from the documents.
3. The extension enters those values through the Chrome DevTools Protocol (`chrome.debugger`): it clicks, types, and reads the page structure.
4. When a control cannot be addressed in the page structure, the extension uses computer use: the model looks at a screenshot and the extension clicks the place it indicates.
5. Before filling, the extension compares the page with the stored structure. When they differ, it reports the form to the admin.
6. For a property with an incomplete choice list, the extension matches the instance value against the choices the page shows.

## 5. Not specified

- The properties of any particular real form.
- Which Foundry embeddings model writes the readings into Qdrant.
- Which model and which kind of Foundry agent perform the pass. A prompt agent can use only the models Microsoft lists for the Browser automation tool, and that list has no Claude model. The first test decides.
- Stored credentials, one-time codes generated by the system, and signing in without the admin.
- The budget of a run: time, actions, model calls, and how much of it goes to collecting dependent choice lists.
- What happens to instances built from an older model after a new model is published.

## 6. Chosen tools

| Step | Tool |
|---|---|
| Start an interpretation, review and publish the model | Admin page in the web application |
| Interpret an unknown form, once, with every property and every option | Foundry agent with the Browser automation tool |
| Sign in to the site of a form | The admin, in the live view of the Browser automation tool |
| List the controls and choices after each action | A fixed script run by the tool's run-code action |
| Build the model from the recorded states | Claude Sonnet 5.5 on Foundry |
| Read each document | Azure Content Understanding, standard mode |
| Keep the readings | Qdrant |
| Build every instance, including subclasses, from the whole set of readings | Claude Haiku 5.5 on Foundry (`claude-haiku-5-5`) |
| Enter the selected instance into the user's tab | Chrome DevTools Protocol in the extension |
| Enter a value when the control is not in the page structure | Computer use, executed by the extension |
