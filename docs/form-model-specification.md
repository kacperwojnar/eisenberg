# Form model module

Specification of what the system does. It interprets a form into a model, extracts matching data from documents into many instances of that model, and fills the form from the instance the user selects in the extension.

Section 6 states what a later tool choice has to be able to do.

## 1. Terms

**Form.** Any page a user fills in. The page structure is not known in advance, and forms are not limited to one site.

**Model.** The shape produced by interpreting a form. It defines a set of properties and holds no values of its own.

**Property.** One field of a model. It has a name and a description of the fact it holds. It is not the question text shown on the page. Where the form offers choices, the property includes those choices.

**Instance.** One record of a model. It holds the values for that model's properties. A model has many instances.

**Document.** A file the user has imported. Its content can be anything. There is no fixed list of document types.

## 2. Interpreting a form

The model is built from the form.

1. The admin maintains these forms for that admin's users.
2. Each maintained form is interpreted into one model.
3. Interpretation is an AI task. It operates the live page: it opens a control when that is required, and it goes through the options so that every property is found, including properties and options that are not on the first screen.
4. The visible question on the page is not stored as the property. Each property has a name and a description of the fact, detailed enough that data in a document can be judged against it.
5. The interpretation records every option the form offers for a property.
6. The model is kept and reused. The form is interpreted again when it no longer matches the stored model, or when something new appears. Filling a form does not itself start an interpretation.

## 3. Documents and instances

What an instance contains depends on the documents. The model decides what is looked for.

1. Documents are scanned against the model. Data that fits the model is extracted into instances.
2. One model produces many instances.
3. Scanning documents and creating instances can run in the background.
4. The user can see the instances on the user page.
5. The user can correct an instance and can extend it.
6. More than one strategy may be used to decide which document data fits a property. No single strategy is fixed by this specification.

## 4. Extension

1. The form is filled in the browser extension.
2. The user selects one instance of the appropriate model. That instance already holds the values extracted from the documents.
3. The form is filled from the selected instance.
4. How the extension enters those values into the page is not specified. The page action can be typing into a field, clicking a choice, or another action.

## 5. Not specified

- The strategy that decides which part of a document fits a property.
- The properties of any particular real form.
- How the extension enters the selected values into the page.
- The products used for interpretation, extraction, storage, and filling. This specification does not choose them.

## 6. What the tools have to support

- Operate a live form, open closed controls, and collect every property and every option, including those revealed only after another choice.
- Store the result as a model: named properties, a description for each property, and the options found on the form.
- Store the models maintained by an admin, and interpret a form again when it no longer matches or something new appears.
- Scan documents against a model, in the background, and produce many instances.
- Show those instances on the user page, and let the user correct and extend them.
- In the extension, let the user choose one instance and fill the current form from it. The way the values are entered into the page is still open.
