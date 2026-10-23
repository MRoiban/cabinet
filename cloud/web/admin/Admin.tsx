import { useState } from "react";
import { createClient } from "bedrock/client";
import { BedrockProvider, useQuery, useUser } from "bedrock/react";
import { Alert, Badge, Button, Card, EmptyState, EmptyStateActions, EmptyStateDescription, EmptyStateIcon, EmptyStateTitle, Icon, SignInGate, Skeleton, UserMenu } from "@bedrock/ui";
import { TopBar } from "../components/TopBar";
import { GameArt } from "../components/GameArt";
import { appName, bytes, count, hours } from "../lib/format";
import type { Overview as Data, Pebble } from "./types";
import { UsersTable } from "./UsersTable";
import { UserSheet } from "./UserSheet";
import { NewUserDialog } from "./NewUserDialog";

const client = createClient<Pebble>();

export function Admin() {
  return <BedrockProvider client={client}>
    <TopBar><span className="cab-desc cab-hide-narrow">{location.host}</span><UserMenu client={client} /></TopBar>
    <SignInGate client={client} name="Cabinet Cloud"><Retrying /></SignInGate>
  </BedrockProvider>;
}
// Remounting re-subscribes, which is the retry for a failed overview.
function Retrying() {
  const [attempt, setAttempt] = useState(0);
  return <Overview key={attempt} retry={() => setAttempt(n => n + 1)} />;
}

function Overview({ retry }: { retry: () => void }) {
  const { data, error, isLoading } = useQuery<Pebble, "adminOverview">("adminOverview", undefined);
  const [selected, setSelected] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  if (error?.code === "FORBIDDEN") return <Forbidden />;
  const empty = data && !data.users.length;
  return <>
    <main className="cab-page">
      <div className="cab-pagehead">
        <div><h1>Users</h1><p className="cab-desc">People who sync saves, achievements and playtime through this server.</p></div>
        {!empty && <Button onClick={() => setCreating(true)}><Icon name="add" />New user</Button>}
      </div>
      {error && <Alert tone="danger" title="Couldn’t load users" description={error.message}
        actions={<Button size="sm" variant="secondary" onClick={retry}><Icon name="refresh" />Retry</Button>} />}
      {isLoading && <Loading />}
      {empty && <Card className="cab-empty"><EmptyState size="lg"><EmptyStateIcon><Icon name="cloud" /></EmptyStateIcon>
        <EmptyStateTitle>No users yet</EmptyStateTitle>
        <EmptyStateDescription>Create a user, then share their connect link or a pairing code. Their saves, achievements and playtime will appear here.</EmptyStateDescription>
        <EmptyStateActions><Button onClick={() => setCreating(true)}><Icon name="add" />New user</Button></EmptyStateActions></EmptyState></Card>}
      {data && !empty && <>
        <Tiles data={data} />
        {data.playing.length > 0 && <Playing playing={data.playing} />}
        <Card className="cab-table-card"><UsersTable users={data.users} quota={data.quota} selected={selected} onSelect={setSelected} /></Card>
      </>}
    </main>
    <UserSheet id={selected} onClose={() => setSelected(null)} />
    <NewUserDialog open={creating} onOpenChange={setCreating} />
  </>;
}

function Tiles({ data: { totals } }: { data: Data }) {
  const tiles = [
    ["Users", count(totals.users), `${count(totals.activeThisWeek)} active this week`],
    ["Games tracked", count(totals.games), `${count(totals.achievements)} achievements unlocked`],
    ["Playtime", hours(totals.playtimeMinutes), "all users"],
    ["Steam Cloud", bytes(totals.cloud.bytes), `${count(totals.cloud.files)} files`],
  ];
  return <div className="cab-tiles">{tiles.map(([label, value, sub]) => <Card key={label} className="cab-tile">
    <div className="cab-desc">{label}</div><div className="cab-tile-value">{value}</div><div className="cab-muted cab-small">{sub}</div>
  </Card>)}</div>;
}

function Playing({ playing }: { playing: Data["playing"] }) {
  return <section>
    <div className="cab-row-between cab-section-title"><h2>Playing now</h2><Badge tone="info" icon={<span className="cab-dot" />}>{playing.length} live</Badge></div>
    <div className="cab-playing">{playing.map(row => <div key={`${row.userId}-${row.appId}`} className="cab-play">
      <GameArt appid={row.appId} className="cab-art-sm" />
      <div className="cab-min"><div className="cab-strong cab-truncate">{appName(row.appId, row.name)}</div><div className="cab-desc cab-small">{row.userName}</div></div>
    </div>)}</div>
  </section>;
}

function Loading() {
  return <div aria-busy="true" aria-label="Loading users" className="cab-stack-lg">
    <div className="cab-tiles">{[0, 1, 2, 3].map(i => <Skeleton key={i} shape="rect" style={{ height: 96 }} />)}</div>
    <Card className="cab-table-card cab-stack">{[0, 1, 2, 3, 4].map(i => <div key={i} className="cab-skeleton-row">
      <Skeleton shape="circle" style={{ width: 28, height: 28 }} /><Skeleton style={{ width: "30%" }} /><Skeleton style={{ width: "15%" }} />
    </div>)}</Card>
  </div>;
}

function Forbidden() {
  const { user } = useUser();
  return <main className="cab-page"><Card className="cab-empty"><EmptyState size="lg"><EmptyStateIcon><Icon name="circle-slash" /></EmptyStateIcon>
    <EmptyStateTitle>This account can’t manage Cabinet</EmptyStateTitle>
    <EmptyStateDescription>{user?.email ? `${user.email} is not a creator on this server. ` : ""}Sign in with a Bedrock creator account, or ask the server owner to add you.</EmptyStateDescription>
    <EmptyStateActions><Button variant="secondary" onClick={() => void client.logout().then(() => location.reload())}><Icon name="sign-out" />Sign out</Button></EmptyStateActions>
  </EmptyState></Card></main>;
}
