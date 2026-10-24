import { useEffect, useState, type ReactNode } from "react";
import { Alert, Button, Icon, Meter, SheetBody, SheetDescription, SheetHeader, SheetTitle, Skeleton } from "@bedrock/ui";
import { GameArt } from "../components/GameArt";
import { appName, count } from "../lib/format";
import type { UserDetail } from "./types";

type Achievement = { id: string; name: string; description: string; icon: string | null; iconGray: string | null;
  hidden: boolean; percent: number | null; unlockedAt: number | null };
type Result = { appid: string; name: string | null; listed: boolean; fetchedAt: number | null; achievements: Achievement[] };

const iconUrl = (appid: string, file: string) => `https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/${appid}/${file}`;
const date = (time: number) => new Date(time).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" });
const percent = (value: number) => `${value < 1 ? value.toFixed(1) : Math.round(value)}% of players`;

export function Achievements({ userId, game, onBack, close }: {
  userId: string; game: UserDetail["games"][number]; onBack: () => void; close: ReactNode;
}) {
  const [result, setResult] = useState<Result | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revealed, setRevealed] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    fetch(`/api/admin/achievements?user=${encodeURIComponent(userId)}&appid=${encodeURIComponent(game.appid)}`,
      { credentials: "same-origin", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(response.status === 403 ? "This account can’t manage Cabinet." : `The server answered ${response.status}.`);
        setResult(await response.json() as Result);
      })
      .catch(cause => { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "Request failed."); });
    return () => controller.abort();
  }, [userId, game.appid]);

  const name = appName(game.appid, game.name);
  const all = result?.achievements ?? [];
  const unlocked = all.filter(item => item.unlockedAt).sort((a, b) => b.unlockedAt! - a.unlockedAt!);
  const locked = all.filter(item => !item.unlockedAt).sort((a, b) => (b.percent ?? -1) - (a.percent ?? -1));
  const secret = locked.filter(item => item.hidden), open = locked.filter(item => !item.hidden);
  return <>
    <SheetHeader className="cab-sheethead">
      <Button variant="ghost" size="icon-sm" aria-label="Back to games" onClick={onBack}><Icon name="arrow-left" /></Button>
      <div className="cab-grow cab-min">
        <SheetTitle className="cab-sheet-title cab-truncate">{name}</SheetTitle>
        <SheetDescription className="cab-desc">{result
          ? `${count(unlocked.length)} of ${count(all.length)} achievements unlocked`
          : "Achievements"}</SheetDescription>
      </div>
      {close}
    </SheetHeader>
    <SheetBody className="cab-sheet-body">
      <GameArt appid={game.appid} />
      {error && <Alert tone="danger" title="Couldn’t load achievements" description={error} />}
      {!result && !error && [0, 1, 2, 3].map(i => <Skeleton key={i} shape="rect" style={{ height: 52 }} />)}
      {result && <>
        {all.length > 0 && <Meter className="cab-meter-lg" tone="success" value={unlocked.length} max={all.length}
          aria-label={`${unlocked.length} of ${all.length} achievements unlocked`} />}
        {!result.listed && <Alert tone="warning" title="Steam’s achievement list isn’t available"
          description="Showing only unlocked achievements, by their internal names. The list is retried on the next visit." />}
        {result.listed && !all.length && <p className="cab-desc cab-small">This game has no Steam achievements.</p>}
        {unlocked.length > 0 && <List title="Unlocked" items={unlocked} appid={game.appid}
          detail={item => `Unlocked ${date(item.unlockedAt!)}`} />}
        {open.length > 0 && <List title="Locked" items={open} appid={game.appid}
          detail={item => item.percent === null ? null : percent(item.percent)} />}
        {secret.length > 0 && (revealed
          ? <List title="Hidden" items={secret} appid={game.appid} detail={item => item.percent === null ? null : percent(item.percent)} />
          : <section className="cab-hidden-ach">
              <Icon name="eye-closed" className="cab-muted" />
              <span className="cab-grow">{secret.length === 1 ? "1 hidden achievement" : `${count(secret.length)} hidden achievements`} still locked</span>
              <Button size="sm" variant="ghost" onClick={() => setRevealed(true)}>Reveal</Button>
            </section>)}
      </>}
    </SheetBody>
  </>;
}

function List({ title, items, appid, detail }: { title: string; items: Achievement[]; appid: string; detail: (item: Achievement) => string | null }) {
  return <section>
    <div className="cab-row-between cab-section-title"><h3>{title}</h3><span className="cab-desc">{count(items.length)}</span></div>
    <ul className="cab-achievements">{items.map(item => {
      const file = item.unlockedAt ? item.icon : item.iconGray ?? item.icon;
      return <li key={item.id} className={`cab-ach${item.unlockedAt ? "" : " cab-ach-locked"}`}>
        {file ? <img className="cab-ach-icon" src={iconUrl(appid, file)} alt="" loading="lazy" />
          : <span className="cab-ach-icon cab-art-missing" aria-hidden="true"><Icon name={item.unlockedAt ? "pass-filled" : "lock"} /></span>}
        <div className="cab-min">
          <div className="cab-strong">{item.name}</div>
          {item.description && <div className="cab-desc cab-small">{item.description}</div>}
          {detail(item) && <div className="cab-muted cab-small">{detail(item)}</div>}
        </div>
      </li>;
    })}</ul>
  </section>;
}
