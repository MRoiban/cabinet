import { useState, type FormEvent } from "react";
import { useMutation } from "bedrock/react";
import { Alert, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Field, FieldDescription, FieldError, Input, Label } from "@bedrock/ui";
import { CopyField } from "../components/CopyField";
import { PairingCode } from "../components/PairingCode";
import { linkFor, type Created, type Pebble } from "./types";

export function NewUserDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const create = useMutation<Pebble, "createUser">("createUser"), pairing = useMutation<Pebble, "pairingCode">("pairingCode");
  const [name, setName] = useState("");
  const [created, setCreated] = useState<Created | null>(null);
  function change(next: boolean) {
    onOpenChange(next);
    if (!next) { setName(""); setCreated(null); }
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;
    try { setCreated(await create.mutate({ name: name.trim() })); } catch { /* shown by FieldError */ }
  }
  const renew = () => created && void pairing.mutate({ id: created.user.id }).then(code => setCreated({ ...created, ...code }), () => {});
  return <Dialog open={open} onOpenChange={change}>
    <DialogContent className="cab-dialog">
      {created ? <>
        <DialogHeader><DialogTitle>{created.user.name} is ready</DialogTitle><DialogDescription>Share one of these. They’re shown only now.</DialogDescription></DialogHeader>
        <Field><Label>Connect link</Label><CopyField value={linkFor(created.link)} label="Copy connect link" copied="Link copied" />
          <FieldDescription>For a Windows PC with Cabinet, or a phone.</FieldDescription></Field>
        <PairingCode code={created.code} expiresAt={created.expiresAt} header="For a Steam Deck" renewing={pairing.isPending} onRenew={renew} />
        <Alert tone="warning" title={`Lost them? Open ${created.user.name} and create a new connect link or pairing code.`} />
        <DialogFooter><Button onClick={() => change(false)}>Done</Button></DialogFooter>
      </> : <form onSubmit={event => void submit(event)} className="cab-stack-lg">
        <DialogHeader><DialogTitle>New user</DialogTitle><DialogDescription>They get a connect link and a pairing code to set up their devices.</DialogDescription></DialogHeader>
        <Field invalid={!!create.error} required><Label>Name</Label>
          <Input autoFocus maxLength={200} value={name} onChange={event => setName(event.target.value)} placeholder="Sam" />
          {create.error && <FieldError>{create.error.message}</FieldError>}</Field>
        <DialogFooter><Button variant="secondary" onClick={() => change(false)}>Cancel</Button>
          <Button type="submit" loading={create.isPending} disabled={!name.trim()}>Create user</Button></DialogFooter>
      </form>}
    </DialogContent>
  </Dialog>;
}
