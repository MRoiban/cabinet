import { useState, type FormEvent } from "react";
import { useMutation, useQuery } from "bedrock/react";
import { Alert, AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter,
  AlertDialogHeader, AlertDialogTitle, Badge, Button, Card, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader,
  DialogTitle, Field, FieldDescription, FieldError, Icon, Input, Label, Meter, Table, TableBody, TableCell, TableHead, TableHeader,
  TableRow, useToast } from "@bedrock/ui";
import { ago, appName, count } from "../lib/format";
import type { HubcapView, Pebble } from "./types";

type Key = HubcapView["keys"][number];
type Job = HubcapView["jobs"][number];
const KEY_FORMAT = /^smm_[0-9a-f]{96}$/;

const ERRORS: Record<string, string> = {
  no_manifest: "Hubcap has no manifest", quota_exhausted: "Daily limit reached", key_expired: "Key expired",
  key_rejected: "Key rejected", relay_disabled: "No usable key", hubcap_unavailable: "Hubcap unavailable",
  invalid_package: "Package refused", stats_unavailable: "Stats unavailable", hubcap_forbidden: "Blocked by Hubcap (key still valid)",
  key_unreadable: "Can’t decrypt (server secret changed)",
};
const errorText = (code: string | null) => code ? ERRORS[code] ?? code : "";

function KeyState({ row }: { row: Key }) {
  if (row.state === "usable") return <Badge tone="success" icon={<span className="cab-dot" />}>{row.checkedAt ? "Usable" : "Checking…"}</Badge>;
  if (row.state === "exhausted") return <Badge tone="warning" icon={<span className="cab-dot" />}>Limit reached</Badge>;
  return <Badge tone="danger" icon={<span className="cab-dot" />}>{row.disabled === "expired" ? "Expired" : row.disabled === "unreadable" ? "Can’t decrypt" : "Rejected"}</Badge>;
}
function JobState({ job }: { job: Job }) {
  if (job.state === "queued") return <Badge>Queued #{job.position}</Badge>;
  if (job.state === "running") return <Badge tone="info" icon={<span className="cab-dot" />}>Fetching</Badge>;
  if (job.state === "ready") return <Badge tone="success">Fetched</Badge>;
  if (job.state === "upstream_not_newer") return <Badge>Nothing newer</Badge>;
  return <Badge tone="danger">{errorText(job.error)}</Badge>;
}

/** /admin: the owner's Hubcap keys and the relay queue. Keys come back masked; the full key is only typed in. */
export function HubcapPanel() {
  const { data, error } = useQuery<Pebble, "adminHubcap">("adminHubcap", undefined);
  const remove = useMutation<Pebble, "removeHubcapKey">("removeHubcapKey");
  const { toast } = useToast();
  const [editing, setEditing] = useState<Key | "new" | null>(null);
  const [removing, setRemoving] = useState<Key | null>(null);
  const [checking, setChecking] = useState(false);
  async function check() {
    setChecking(true);
    try {
      const response = await fetch("/api/admin/hubcap/check", { method: "POST" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      toast({ title: "Keys checked", tone: "success" });
    } catch (cause) { toast({ title: "Couldn’t check the keys", description: cause instanceof Error ? cause.message : undefined, tone: "danger" }); }
    finally { setChecking(false); }
  }
  if (error?.code === "FORBIDDEN") return null;
  return <section className="cab-stack">
    <div className="cab-row-between cab-section-title">
      <div><h2>Hubcap relay</h2><p className="cab-desc">Users get Hubcap Lua configs through this server with these keys. Each game is fetched once into the shared cache.</p></div>
      <div className="cab-inline">{data && data.keys.length > 0 && <Button variant="secondary" loading={checking} disabled={data.secret !== "ok"} onClick={() => void check()}><Icon name="refresh" />Check keys</Button>}
        <Button disabled={!data || data.secret !== "ok"} onClick={() => setEditing("new")}><Icon name="add" />Add key</Button></div>
    </div>
    {error && <Alert tone="danger" title="Couldn’t load the relay" description={error.message} />}
    {data && data.secret !== "ok" && <Alert tone="danger" title={data.secret === "missing" ? "Server secret not set" : "Server secret is invalid"}
      description="Keys are encrypted with the pebble secret HUBCAP_KEY_SECRET (32 random bytes, base64). Until it is set, the relay is off and keys can’t be saved. See cloud/README.md, “Hubcap relay”." />}
    {data && data.secret === "ok" && !data.enabled && data.keys.length > 0 && <Alert tone="warning" title={`Relay paused: ${errorText(data.failure).toLowerCase()}`}
      description="Requests fail at once until a key can make requests again. Stats are re-checked every few minutes." />}
    {data && <Card className="cab-table-card">{data.keys.length === 0
      ? <p className="cab-desc cab-pad">No keys yet. Add a Hubcap API key to turn the relay on.</p>
      : <Table className="cab-table">
        <TableHeader><TableRow><TableHead>Key</TableHead><TableHead>Status</TableHead><TableHead className="cab-hide-narrow">Today</TableHead>
          <TableHead className="cab-hide-narrow">Expires</TableHead><TableHead className="cab-hide-narrow">Last error</TableHead><TableHead /></TableRow></TableHeader>
        <TableBody>{data.keys.map(row => <TableRow key={row.id}>
          <TableCell><div className="cab-strong">{row.label || "Unlabelled"}</div><code className="cab-desc cab-small">{row.mask}</code></TableCell>
          <TableCell><KeyState row={row} /></TableCell>
          <TableCell className="cab-usage cab-hide-narrow">{row.stats
            ? <><span className="cab-desc">{count(row.stats.dailyUsage)} of {count(row.stats.dailyLimit)}{row.stats.canMakeRequests ? "" : " (blocked)"}</span>
              <Meter className="cab-meter-sm" tone={row.stats.dailyUsage >= row.stats.dailyLimit ? "warning" : "info"} value={row.stats.dailyUsage} max={row.stats.dailyLimit}
                aria-label={`${row.stats.dailyUsage} of ${row.stats.dailyLimit} downloads today`} /></>
            : <span className="cab-muted">—</span>}</TableCell>
          <TableCell className="cab-desc cab-hide-narrow">{row.stats?.expiresAt ? new Date(row.stats.expiresAt).toLocaleDateString() : "—"}</TableCell>
          <TableCell className="cab-desc cab-hide-narrow">{row.lastError ? `${errorText(row.lastError)}, ${ago(row.lastErrorAt).toLowerCase()}` : "—"}</TableCell>
          <TableCell className="cab-num"><div className="cab-inline">
            <Button size="sm" variant="secondary" onClick={() => setEditing(row)}>Edit</Button>
            <Button size="sm" variant="secondary" onClick={() => setRemoving(row)} aria-label={`Remove ${row.label || row.mask}`}><Icon name="trash" /></Button>
          </div></TableCell>
        </TableRow>)}</TableBody>
      </Table>}</Card>}
    {data && data.jobs.length > 0 && <Card className="cab-table-card">
      <Table className="cab-table">
        <TableHeader><TableRow><TableHead>Game</TableHead><TableHead>State</TableHead><TableHead className="cab-hide-narrow">Requested by</TableHead><TableHead className="cab-hide-narrow">When</TableHead></TableRow></TableHeader>
        <TableBody>{data.jobs.map(job => <TableRow key={job.id}>
          <TableCell><div className="cab-strong cab-truncate">{appName(job.appid, job.name)}</div><div className="cab-desc cab-small">{job.appid}</div></TableCell>
          <TableCell><JobState job={job} /></TableCell>
          <TableCell className="cab-desc cab-hide-narrow">{job.requesters.join(", ") || "—"}</TableCell>
          <TableCell className="cab-desc cab-hide-narrow">{ago(job.finishedAt ?? job.createdAt)}</TableCell>
        </TableRow>)}</TableBody>
      </Table>
    </Card>}
    <KeyDialog target={editing} onClose={() => setEditing(null)} />
    <AlertDialog open={!!removing} onOpenChange={open => { if (!open) setRemoving(null); }}>
      <AlertDialogContent>
        <AlertDialogHeader><AlertDialogTitle>Remove {removing?.label || removing?.mask}?</AlertDialogTitle>
          <AlertDialogDescription>The relay stops using this key. Games already fetched stay in the shared cache.</AlertDialogDescription></AlertDialogHeader>
        <AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction onClick={() => removing && void remove.mutate({ id: removing.id }).then(() => toast({ title: "Key removed", tone: "success" }),
            (cause: unknown) => toast({ title: "Couldn’t remove the key", description: cause instanceof Error ? cause.message : undefined, tone: "danger" }))}>Remove key</AlertDialogAction></AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </section>;
}

/** Add a key, or relabel / replace one. The key field is write-only: it starts empty and is never filled from the server. */
function KeyDialog({ target, onClose }: { target: Key | "new" | null; onClose: () => void }) {
  const add = useMutation<Pebble, "addHubcapKey">("addHubcapKey"), update = useMutation<Pebble, "updateHubcapKey">("updateHubcapKey");
  const [key, setKey] = useState(""), [label, setLabel] = useState(""), [opened, setOpened] = useState<typeof target>(null);
  if (target !== opened) { setOpened(target); setKey(""); setLabel(target && target !== "new" ? target.label ?? "" : ""); }
  const isNew = target === "new", trimmed = key.trim();
  const formatOk = KEY_FORMAT.test(trimmed), pending = add.isPending || update.isPending, failure = add.error ?? update.error;
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (isNew ? !formatOk : trimmed !== "" && !formatOk) return;
    try {
      if (isNew) await add.mutate({ key: trimmed, label: label.trim() });
      else if (target) await update.mutate({ id: target.id, label: label.trim(), ...(trimmed ? { key: trimmed } : {}) });
      setKey("");
      onClose();
    } catch { /* shown below */ }
  }
  return <Dialog open={!!target} onOpenChange={open => { if (!open) { setKey(""); onClose(); } }}>
    <DialogContent className="cab-dialog">
      <form onSubmit={event => void submit(event)} className="cab-stack-lg">
        <DialogHeader><DialogTitle>{typeof target === "object" && target ? `Edit ${target.label || target.mask}` : "Add a Hubcap key"}</DialogTitle>
          <DialogDescription>The key stays on this server. After saving, only its last four characters are shown.</DialogDescription></DialogHeader>
        <Field><Label>Label</Label><Input maxLength={64} value={label} onChange={event => setLabel(event.target.value)} placeholder="Main account" />
          <FieldDescription>Optional, to tell keys apart.</FieldDescription></Field>
        <Field invalid={trimmed !== "" && !formatOk} required={isNew}><Label>{isNew ? "API key" : "Replace key"}</Label>
          <Input type="password" autoComplete="off" spellCheck={false} value={key} onChange={event => setKey(event.target.value)} placeholder="smm_…" />
          {trimmed !== "" && !formatOk ? <FieldError>A Hubcap key is smm_ followed by 96 lowercase hex characters.</FieldError>
            : <FieldDescription>{isNew ? "From your Hubcap account." : "Leave empty to keep the current key."}</FieldDescription>}</Field>
        {failure && <Alert tone="danger" title={failure.message} />}
        <DialogFooter><Button variant="secondary" onClick={() => { setKey(""); onClose(); }}>Cancel</Button>
          <Button type="submit" loading={pending} disabled={isNew ? !formatOk : trimmed !== "" && !formatOk}>{isNew ? "Add key" : "Save"}</Button></DialogFooter>
      </form>
    </DialogContent>
  </Dialog>;
}
