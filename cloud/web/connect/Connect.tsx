import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, EmptyState, EmptyStateDescription, EmptyStateIcon, EmptyStateTitle, Icon, Skeleton, ToggleGroup, ToggleGroupItem } from "@bedrock/ui";
import { TopBar } from "../components/TopBar";
import { UserAvatar } from "../components/UserAvatar";
import { defaultLang, strings, type Lang } from "../lib/i18n";
import { loadPortal, TOKEN, Unauthorized, type Portal } from "./api";
import { Devices } from "./Devices";
import { Games, Storage } from "./Games";

type View = { state: "loading" } | { state: "invalid" } | { state: "offline" } | { state: "ready"; portal: Portal };
const tokenFromHash = () => decodeURIComponent(location.hash.slice(1));

export function Connect() {
  const [lang, setLang] = useState<Lang>(defaultLang);
  const [token, setToken] = useState(tokenFromHash);
  const [view, setView] = useState<View>({ state: "loading" });
  const t = strings[lang];
  const load = useCallback(() => {
    if (!TOKEN.test(token)) return setView({ state: "invalid" });
    setView({ state: "loading" });
    loadPortal(token).then(portal => setView({ state: "ready", portal }),
      error => setView({ state: error instanceof Unauthorized ? "invalid" : "offline" }));
  }, [token]);
  useEffect(load, [load]);
  useEffect(() => {
    const changed = () => setToken(tokenFromHash());
    addEventListener("hashchange", changed);
    return () => removeEventListener("hashchange", changed);
  }, []);
  useEffect(() => { document.documentElement.lang = lang; }, [lang]);

  return <>
    <TopBar>
      <ToggleGroup className="cab-lang" type="single" value={lang} onValueChange={(next: string) => next && setLang(next as Lang)} aria-label={t.language}>
        <ToggleGroupItem value="en">EN</ToggleGroupItem><ToggleGroupItem value="fr">FR</ToggleGroupItem>
      </ToggleGroup>
    </TopBar>
    <main className="cab-page cab-portal" aria-busy={view.state === "loading"}>
      {view.state === "loading" && <PortalSkeleton />}
      {view.state === "invalid" && <Card className="cab-empty"><EmptyState size="lg"><EmptyStateIcon><Icon name="link" /></EmptyStateIcon>
        <EmptyStateTitle>{t.invalid}</EmptyStateTitle><EmptyStateDescription>{t.invalidHint}</EmptyStateDescription></EmptyState></Card>}
      {view.state === "offline" && <Alert tone="danger" title={t.offline} description={t.offlineHint}
        actions={<Button size="sm" variant="secondary" onClick={load}><Icon name="refresh" />{t.retry}</Button>} />}
      {view.state === "ready" && <>
        <div className="cab-hero">
          <UserAvatar name={view.portal.name} seed={view.portal.name} size="lg" />
          <div><h1>{t.hi(view.portal.name)}</h1><p className="cab-desc">{t.intro}</p></div>
        </div>
        <Devices token={token} t={t} />
        <Games games={view.portal.games} t={t} lang={lang} />
        <Storage usage={view.portal.usage} t={t} lang={lang} />
      </>}
    </main>
  </>;
}
function PortalSkeleton() {
  return <>
    <div className="cab-hero"><Skeleton shape="circle" style={{ width: 48, height: 48 }} />
      <div className="cab-stack cab-grow"><Skeleton style={{ width: "30%", height: 22 }} /><Skeleton style={{ width: "70%" }} /></div></div>
    <div className="cab-devices"><Skeleton shape="rect" style={{ height: 220 }} /><Skeleton shape="rect" style={{ height: 220 }} /></div>
    <div className="cab-grid">{[0, 1, 2].map(i => <Skeleton key={i} shape="rect" style={{ height: 200 }} />)}</div>
  </>;
}
