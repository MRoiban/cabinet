import { Badge, Meter, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@bedrock/ui";
import { UserAvatar } from "../components/UserAvatar";
import { ago, bytes, count, hours, plural } from "../lib/format";
import type { UserRow } from "./types";

export function StatusBadge({ status }: { status: UserRow["status"] }) {
  if (status === "active") return <Badge tone="success" icon={<span className="cab-dot" />}>Active</Badge>;
  if (status === "revoked") return <Badge tone="danger" icon={<span className="cab-dot" />}>Revoked</Badge>;
  return <Badge>Not connected</Badge>;
}
export const connectionsText = (user: Pick<UserRow, "status" | "connections">) =>
  user.status === "revoked" ? "Access revoked" : user.status === "unconnected" ? "Not connected yet" : plural(user.connections, "connection", "connections");

export function UsersTable({ users, quota, selected, onSelect }: { users: UserRow[]; quota: number; selected: string | null; onSelect: (id: string) => void }) {
  return <Table className="cab-table">
    <TableHeader><TableRow>
      <TableHead>Name</TableHead><TableHead className="cab-hide-narrow">Last seen</TableHead><TableHead>Status</TableHead>
      <TableHead className="cab-num cab-hide-narrow">Games</TableHead><TableHead className="cab-num cab-hide-narrow">Playtime</TableHead>
      <TableHead className="cab-hide-narrow">Steam Cloud</TableHead>
    </TableRow></TableHeader>
    <TableBody>{users.map(user => {
      const quiet = !user.games;
      return <TableRow key={user.id} className="cab-row" data-user-id={user.id} selected={user.id === selected} tabIndex={0} aria-label={`${user.name}, open details`}
        onClick={() => onSelect(user.id)} onKeyDown={event => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); onSelect(user.id); } }}>
        <TableCell><div className="cab-user">
          <UserAvatar name={user.name} seed={user.id} muted={user.status !== "active"} size="sm" />
          <div className="cab-min"><div className="cab-strong cab-truncate">{user.name}</div><div className="cab-desc cab-small">{connectionsText(user)}</div></div>
        </div></TableCell>
        <TableCell className="cab-desc cab-hide-narrow">{ago(user.lastSeen)}</TableCell>
        <TableCell><StatusBadge status={user.status} /></TableCell>
        <TableCell className={`cab-num cab-hide-narrow${quiet ? " cab-muted" : ""}`}>{quiet ? "—" : count(user.games)}</TableCell>
        <TableCell className={`cab-num cab-hide-narrow${quiet ? " cab-muted" : ""}`}>{quiet ? "—" : hours(user.playtimeMinutes)}</TableCell>
        <TableCell className="cab-usage cab-hide-narrow">{user.cloud.files
          ? <><span className="cab-desc">{bytes(user.cloud.bytes)} of {bytes(quota)}</span>
              <Meter className="cab-meter-sm" tone="info" value={user.cloud.bytes} max={quota} aria-label={`Steam Cloud: ${bytes(user.cloud.bytes)} of ${bytes(quota)}`} /></>
          : <span className="cab-muted">Empty</span>}</TableCell>
      </TableRow>;
    })}</TableBody>
  </Table>;
}
