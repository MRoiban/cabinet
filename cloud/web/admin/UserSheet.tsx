import { useState } from "react";
import { useMutation, useQuery } from "bedrock/react";
import { Alert, AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogIcon, AlertDialogTitle,
  Badge, Button, Icon, Meter, Sheet, SheetBody, SheetClose, SheetContent, SheetDescription, SheetHeader, SheetTitle, Skeleton, useToast } from "@bedrock/ui";
import { CopyField } from "../components/CopyField";
import { GameArt } from "../components/GameArt";
import { PairingCode, pairingText } from "../components/PairingCode";
import { UserAvatar } from "../components/UserAvatar";
import { ago, appName, bytes, count, hours, plural, shortDate } from "../lib/format";
import { linkFor, type Pebble, type UserDetail } from "./types";
import { connectionsText } from "./UsersTable";

export function UserSheet({ id, onClose }: { id: string | null; onClose: () => void }) {
  // Keep the last user rendered while the sheet animates closed.
  const [shown, setShown] = useState(id);
  if (id && id !== shown) setShown(id);
  return <Sheet open={!!id} onOpenChange={open => { if (!open) onClose(); }}>
    <SheetContent side="right" className="cab-sheet" onCloseAutoFocus={event => {
      // There is no SheetTrigger: hand focus back to the row that opened the sheet.
      event.preventDefault();
      document.querySelector<HTMLElement>(`[data-user-id="${shown}"]`)?.focus();
    }}>{shown && <Detail key={shown} id={shown} />}</SheetContent>
  </Sheet>;
}

type Confirm = "link" | "revoke" | "delete";
const CONFIRM: Record<Confirm, { title: (name: string) => string; body: string; action: string }> = {
  link: { title: name => `Create a new connect link for ${name}?`, action: "Create link",
    body: "Every device connected now is signed out, including paired Steam Decks. Saves and progress stay on the server." },
  revoke: { title: name => `Revoke access for ${name}?`, action: "Revoke access",
    body: "Every connected device is signed out. Saves and progress stay on the server; a new connect link or pairing code restores access." },
  delete: { title: name => `Delete ${name}’s Steam Cloud files?`, action: "Delete files",
    body: "This permanently removes every file CloudRedirect stored for this user. Their devices upload them again only if they still have local copies." },
};

function Detail({ id }: { id: string }) {
  const { data, error } = useQuery<Pebble, "adminUser">("adminUser", { id });
  const { toast } = useToast();
  const pairing = useMutation<Pebble, "pairingCode">("pairingCode"), relink = useMutation<Pebble, "newConnectLink">("newConnectLink");
  const revoke = useMutation<Pebble, "revokeAccess">("revokeAccess"), wipe = useMutation<Pebble, "deleteCloudFiles">("deleteCloudFiles");
  const [code, setCode] = useState<{ code: string; expiresAt: number } | null>(null);
  const [link, setLink] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<Confirm | null>(null);
  const fail = (title: string) => (cause: unknown) => toast({ title, description: cause instanceof Error ? cause.message : undefined, tone: "danger" });
  const newCode = () => pairing.mutate({ id }).then(setCode, fail("Couldn’t create a pairing code"));
  async function confirmed(kind: Confirm) {
    if (kind === "link") await relink.mutate({ id }).then(result => { setLink(linkFor(result.link)); setCode(null); toast({ title: "New connect link created", tone: "success" }); }, fail("Couldn’t create a link"));
    if (kind === "revoke") await revoke.mutate({ id }).then(() => { setLink(null); setCode(null); toast({ title: "Access revoked", tone: "success" }); }, fail("Couldn’t revoke access"));
    if (kind === "delete") await wipe.mutate({ id }).then(({ deleted }) => toast({ title: `Deleted ${plural(deleted, "file", "files")}`, tone: "success" }), fail("Couldn’t delete files"));
  }
  if (!data) return <>
    <SheetHeader className="cab-sheethead"><SheetTitle className="sr-only">User</SheetTitle><SheetDescription className="sr-only">Loading user details</SheetDescription>
      {error ? <Alert tone="danger" title="Couldn’t load this user" description={error.message} /> : <Skeleton style={{ width: "60%", height: 22 }} />}
      <Close /></SheetHeader>
    {!error && <SheetBody>{[0, 1, 2].map(i => <Skeleton key={i} shape="rect" style={{ height: 59 }} />)}</SheetBody>}
  </>;
  const { user } = data;
  return <>
    <SheetHeader className="cab-sheethead">
      <UserAvatar name={user.name} seed={user.id} muted={user.status !== "active"} size="lg" />
      <div className="cab-grow cab-min">
        <SheetTitle className="cab-sheet-title">{user.name}</SheetTitle>
        <SheetDescription className="cab-desc">Last seen {ago(user.lastSeen).toLowerCase()} · {connectionsText(user).toLowerCase()} · created {shortDate(user.createdAt)}</SheetDescription>
      </div>
      <Close />
    </SheetHeader>
    <SheetBody className="cab-sheet-body">
      <div className="cab-actions">
        <Button size="sm" variant="secondary" loading={pairing.isPending} onClick={() => void newCode()}><Icon name="key" />Pairing code</Button>
        <Button size="sm" variant="secondary" onClick={() => setConfirm("link")}><Icon name="refresh" />New connect link</Button>
        {user.connections > 0 && <Button size="sm" variant="destructive" onClick={() => setConfirm("revoke")}><Icon name="circle-slash" />Revoke access</Button>}
      </div>
      {code && <PairingCode {...code} renewing={pairing.isPending} onRenew={() => void newCode()}
        text={{ ...pairingText, hint: "Type it on the Steam Deck in the LuaTools sidebar or Lumen’s Cloud Saves tab." }} />}
      {link && <div className="cab-codebox">
        <div className="cab-row-between"><h3>Connect link</h3><Badge>Shown once</Badge></div>
        <CopyField value={link} label="Copy connect link" copied="Link copied" />
        <p className="cab-desc cab-small">For a Windows PC with Cabinet, or a phone. Older links no longer work.</p>
      </div>}
      <Games data={data} />
      <section>
        <div className="cab-row-between cab-section-title"><h3>Steam Cloud</h3><span className="cab-desc">{bytes(data.cloud.bytes)} of {bytes(data.quota)}</span></div>
        <Meter className="cab-meter-lg" tone="info" value={data.cloud.bytes} max={data.quota} aria-label={`Steam Cloud: ${bytes(data.cloud.bytes)} of ${bytes(data.quota)}`} />
        <p className="cab-desc cab-small cab-gap-top">{data.cloud.files
          ? `${plural(data.cloud.files, "file", "files")} from ${plural(data.cloudGames, "game", "games")}, kept by CloudRedirect on Linux.`
          : "No files yet. CloudRedirect on Linux keeps Steam Cloud saves here."}</p>
        {data.cloud.files > 0 && <Button size="sm" variant="destructive" className="cab-gap-top-lg" loading={wipe.isPending} onClick={() => setConfirm("delete")}><Icon name="trash" />Delete cloud files…</Button>}
      </section>
    </SheetBody>
    <AlertDialog open={!!confirm} onOpenChange={open => { if (!open) setConfirm(null); }}>
      {confirm && <AlertDialogContent tone={confirm === "link" ? "warning" : "danger"}>
        <AlertDialogHeader><AlertDialogIcon /><AlertDialogTitle>{CONFIRM[confirm].title(user.name)}</AlertDialogTitle>
          <AlertDialogDescription>{CONFIRM[confirm].body}</AlertDialogDescription></AlertDialogHeader>
        <AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction onClick={() => void confirmed(confirm)}>{CONFIRM[confirm].action}</AlertDialogAction></AlertDialogFooter>
      </AlertDialogContent>}
    </AlertDialog>
  </>;
}

function Close() {
  return <SheetClose asChild><Button variant="ghost" size="icon-sm" aria-label="Close"><Icon name="close" /></Button></SheetClose>;
}

function Games({ data }: { data: UserDetail }) {
  return <section>
    <div className="cab-row-between cab-section-title"><h3>Games</h3><span className="cab-desc">{count(data.games.length)}</span></div>
    {data.games.length
      ? <div className="cab-games">{data.games.map(game => <div key={game.appid} className="cab-game">
          <GameArt appid={game.appid} className="cab-art-md" />
          <div className="cab-min"><div className="cab-strong cab-truncate">{appName(game.appid, game.name)}</div>
            <div className="cab-facts">
              {game.achievements > 0 && <span>{plural(game.achievements, "achievement", "achievements")}</span>}
              {game.playtimeMinutes > 0 && <span>{hours(game.playtimeMinutes)}</span>}
              {game.save ? <span>Save {bytes(game.save.size)} · {game.save.deviceName} · {ago(game.save.updatedAt).toLowerCase()}</span>
                : game.cloud && <span>Steam Cloud · {bytes(game.cloud.bytes)}</span>}
            </div></div>
        </div>)}</div>
      : <p className="cab-desc cab-small">Nothing synced yet.</p>}
  </section>;
}
