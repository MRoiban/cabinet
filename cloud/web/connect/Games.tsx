import { Card, EmptyState, EmptyStateDescription, EmptyStateIcon, EmptyStateTitle, Icon, Meter } from "@bedrock/ui";
import { GameArt } from "../components/GameArt";
import { ago, appName, bytes, count, hours } from "../lib/format";
import type { Lang, Strings } from "../lib/i18n";
import type { Portal } from "./api";

export function Games({ games, t, lang }: { games: Portal["games"]; t: Strings; lang: Lang }) {
  const total = games.reduce((sum, game) => sum + game.playtimeMinutes, 0);
  return <section className="cab-section">
    <div className="cab-row-between cab-section-title"><h2>{t.games}</h2>
      {games.length > 0 && <span className="cab-desc">{t.gameCount(games.length)} · {hours(total, lang)}</span>}</div>
    {games.length
      ? <div className="cab-grid">{games.map(game => <Card key={game.appid} className="cab-gcard">
          <GameArt appid={game.appid} />
          <div className="cab-gcard-body">
            <div className="cab-row-between"><h3 className="cab-truncate">{appName(game.appid, game.name)}</h3>
              {game.playtimeMinutes > 0 && <span className="cab-desc cab-nowrap">{hours(game.playtimeMinutes, lang)}</span>}</div>
            {game.achievements > 0 && <div className="cab-desc cab-small">{t.achievements(game.achievements)}</div>}
            {game.save ? <div className="cab-desc cab-small">{t.saveFrom(game.save.deviceName, ago(game.save.updatedAt, lang, t))}</div>
              : game.cloud && <div className="cab-desc cab-small">{t.steamCloud}</div>}
          </div>
        </Card>)}</div>
      : <Card className="cab-empty"><EmptyState><EmptyStateIcon><Icon name="game" /></EmptyStateIcon>
          <EmptyStateTitle>{t.noGames}</EmptyStateTitle><EmptyStateDescription>{t.noGamesHint}</EmptyStateDescription></EmptyState></Card>}
  </section>;
}

export function Storage({ usage, t, lang }: { usage: Portal["usage"]; t: Strings; lang: Lang }) {
  const text = t.of(bytes(usage.bytes, lang), bytes(usage.quota, lang));
  return <Card className="cab-card cab-stack">
    <div className="cab-row-between"><h2>{t.storage}</h2><span className="cab-desc">{text}</span></div>
    <Meter className="cab-meter-lg" value={usage.bytes} max={usage.quota} high={usage.quota * 0.9} optimum={0} tone={usage.bytes > usage.quota * 0.9 ? "warning" : "info"} aria-label={`${t.storage}: ${text}`} />
    <p className="cab-desc cab-small">{t.files(count(usage.files, lang))}</p>
  </Card>;
}
