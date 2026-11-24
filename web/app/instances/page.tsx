"use client";

import { FormEvent, useEffect, useState } from "react";
import { ApiError, api } from "../../lib/api";

type Source = { fileName?: string; page?: number | null; location?: string };
type Property = { name: string; description: string; kind: string; required: boolean; pageLabel: string; options: string[]; incomplete?: boolean };
type Instance = {
  id: string;
  organizationId: string;
  organizationName: string;
  modelId: string;
  formId: string | null;
  formAddress: string;
  typeName: string;
  subclassName: string | null;
  label: string;
  generated: boolean;
  values: Record<string, string>;
  sources: Record<string, Source>;
  properties: Property[];
};
type ModelType = { name: string; subclassOf: string | null; properties: Property[] };
type PublishedModel = { organizationId: string; organizationName: string; modelId: string; address: string; types: ModelType[] };

export default function InstancesPage() {
  const [instances, setInstances] = useState<Instance[]>([]);
  const [selected, setSelected] = useState<Instance | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [label, setLabel] = useState("");
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [models, setModels] = useState<PublishedModel[]>([]);
  const [modelId, setModelId] = useState("");
  const [typeName, setTypeName] = useState("");
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [draftErrors, setDraftErrors] = useState<Record<string, string>>({});
  const [newLabel, setNewLabel] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  async function load() {
    const body = await api<{ instances: Instance[] }>("/form-instances");
    setInstances(body.instances);
    const published = await api<PublishedModel[]>("/published-models");
    setModels(published);
    setModelId((current) => current || published[0]?.modelId || "");
    setTypeName((current) => current || published[0]?.types[0]?.name || "");
    return body.instances;
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  function open(instance: Instance) {
    setSelected(instance);
    setLabel(instance.label);
    setValues(instance.values ?? {});
    setFieldErrors({});
    setNotice("");
    setError("");
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (!selected) return;
    setError("");
    setFieldErrors({});
    try {
      await api(`/form-instances/${selected.id}`, {
        method: "PUT",
        body: JSON.stringify({ label, values })
      });
      const next = await load();
      const updated = next.find((item) => item.id === selected.id);
      if (updated) open(updated);
      setNotice("Record saved. Open the form address, then choose this record in the extension.");
    } catch (e) {
      if (e instanceof ApiError && e.problems.length > 0) {
        const next: Record<string, string> = {};
        const rest: string[] = [];
        for (const problem of e.problems) {
          if (problem.property) next[problem.property] = problem.message;
          else rest.push(problem.message);
        }
        setFieldErrors(next);
        setError(rest.join(" "));
      } else {
        setError((e as Error).message);
      }
    }
  }

  const model = models.find((item) => item.modelId === modelId) || models[0];
  const chosen = model?.types?.find((type) => type.name === typeName) || model?.types?.[0];

  const groups = new Map<string, Instance[]>();
  for (const instance of instances) {
    const key = `${instance.organizationName || "Organization"} · ${instance.formAddress || "No published form"}`;
    groups.set(key, [...(groups.get(key) ?? []), instance]);
  }

  return (
    <main className="wide">
      <h1>Records</h1>
      <p className="note">The extractor builds these from the organization&apos;s documents and the published form. Open the form address in the browser, then choose the record in the extension.</p>
      {error && <p className="failed">{error}</p>}
      {notice && <p className="note">{notice}</p>}
      {models.length > 0 && model && (
        <form className="stack" onSubmit={async (event) => {
          event.preventDefault();
          if (!chosen) return;
          setDraftErrors({});
          setError("");
          try {
            await api("/form-instances", {
              method: "POST",
              body: JSON.stringify({
                organizationId: model.organizationId,
                modelId: model.modelId,
                typeName: chosen.name,
                label: newLabel || chosen.name,
                values: draft
              })
            });
            setNewLabel("");
            setDraft({});
            setNotice("Record added.");
            await load();
          } catch (e) {
            if (e instanceof ApiError && e.problems.length > 0) {
              const next: Record<string, string> = {};
              const rest: string[] = [];
              for (const problem of e.problems) {
                if (problem.property) next[problem.property] = problem.message;
                else rest.push(problem.message);
              }
              setDraftErrors(next);
              setError(rest.join(" "));
            } else {
              setError((e as Error).message);
            }
          }
        }}>
          <h2>Add a record</h2>
          <label>
            Form
            <select value={model.modelId} onChange={(e) => {
              const next = models.find((item) => item.modelId === e.target.value) || models[0];
              setModelId(next.modelId);
              setTypeName(next?.types?.[0]?.name || "");
              setDraft({});
              setDraftErrors({});
            }}>
              {models.map((item) => <option key={item.modelId} value={item.modelId}>{item.organizationName} · {item.address}</option>)}
            </select>
          </label>
          <label>
            Type
            <select value={chosen?.name ?? ""} onChange={(e) => { setTypeName(e.target.value); setDraft({}); setDraftErrors({}); }}>
              {(model.types ?? []).map((type) => (
                <option key={type.name} value={type.name}>{type.subclassOf ? `${type.name} (${type.subclassOf})` : type.name}</option>
              ))}
            </select>
          </label>
          <label>Name<input value={newLabel} onChange={(e) => setNewLabel(e.target.value)} placeholder={chosen?.name || "Record name"} /></label>
          {!chosen && <p className="note">This published model has no types.</p>}
          {chosen?.properties.map((property) => (
            <label key={property.name}>
              {property.pageLabel || property.name}{property.required ? " *" : ""}
              {property.options.length > 0 ? (
                <select value={draft[property.name] ?? ""} onChange={(e) => setDraft((current) => ({ ...current, [property.name]: e.target.value }))}>
                  <option value="">Choose</option>
                  {property.options.map((option) => <option key={option} value={option}>{option}</option>)}
                </select>
              ) : (
                <input value={draft[property.name] ?? ""} onChange={(e) => setDraft((current) => ({ ...current, [property.name]: e.target.value }))} />
              )}
              {draftErrors[property.name] && <span className="failed">{draftErrors[property.name]}</span>}
            </label>
          ))}
          <button type="submit" disabled={!chosen}>Add a record</button>
        </form>
      )}
      <div className="split">
        <div>
          {[...groups.entries()].map(([name, items]) => (
            <section key={name}>
              <h2>{items[0]?.organizationName || "Organization"}</h2>
              <p>{items[0]?.formAddress
                ? <a href={items[0].formAddress}>Open {items[0].formAddress}</a>
                : "No published form address."} Then choose a record in the extension.</p>
              <ul className="record-list">
                {items.map((instance) => (
                  <li key={instance.id}>
                    <button type="button" aria-pressed={selected?.id === instance.id} onClick={() => open(instance)}>
                      <strong>{instance.label || instance.typeName}</strong>
                      <span>{instance.typeName}{instance.subclassName ? ` / ${instance.subclassName}` : ""}{instance.generated ? "" : " · corrected"}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          ))}
          {instances.length === 0 && <p className="note">No records yet. Import a document or add one.</p>}
        </div>
        {selected && (
          <form className="stack" onSubmit={save}>
            <p>Open <a href={selected.formAddress}>{selected.formAddress || "the published form"}</a>, then choose this record in the extension.</p>
            <label>Name<input value={label} onChange={(e) => setLabel(e.target.value)} /></label>
            {selected.properties.map((property) => {
              const source = selected.sources?.[property.name];
              const place = source?.location ? source.location.slice(0, 120) : "";
              return (
                <label key={property.name}>
                  {property.pageLabel || property.name}{property.required ? " *" : ""}
                  {property.description && <span className="note">{property.description}</span>}
                  <span className="note">
                    {source?.fileName
                      ? `From ${source.fileName}${source.page ? `, page ${source.page}` : ""}${place ? `, ${place}` : ""}`
                      : "The reading does not locate this value."}
                  </span>
                  {property.options.length > 0 ? (
                    <select value={values[property.name] ?? ""} onChange={(e) => setValues((current) => ({ ...current, [property.name]: e.target.value }))}>
                      <option value="">Choose</option>
                      {property.options.map((option) => <option key={option} value={option}>{option}</option>)}
                    </select>
                  ) : (
                    <input value={values[property.name] ?? ""} onChange={(e) => setValues((current) => ({ ...current, [property.name]: e.target.value }))} />
                  )}
                  {fieldErrors[property.name] && <span className="failed">{fieldErrors[property.name]}</span>}
                </label>
              );
            })}
            <button type="submit">Save record</button>
          </form>
        )}
      </div>
    </main>
  );
}
