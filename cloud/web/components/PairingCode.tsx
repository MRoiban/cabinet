import { useEffect, useState } from "react";
import { Badge, Button, Icon, Meter } from "@bedrock/ui";
import { PAIRING_TTL } from "../lib/format";

export interface PairingText { title: string; once: string; expiresIn: (time: string) => string; expired: string; renew: string; hint?: string }
export const pairingText: PairingText = {
  title: "Pairing code", once: "Works once", expiresIn: time => `Expires in ${time}`,
  expired: "Expired", renew: "Get a new code",
};

function useNow(until: number) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (now >= until) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [until, now >= until]);
  return now;
}
const clock = (ms: number) => `${Math.floor(ms / 60_000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, "0")}`;

// A one-time code with its live countdown; expiry offers a fresh code.
export function PairingCode({ code, expiresAt, onRenew, renewing = false, compact = false, header = true, text = pairingText }:
  { code: string; expiresAt: number; onRenew: () => void; renewing?: boolean; compact?: boolean; header?: boolean | string; text?: PairingText }) {
  const left = Math.max(0, expiresAt - useNow(expiresAt));
  return <div className={`cab-codebox${compact ? " cab-codebox-compact" : ""}`}>
    {header && <div className="cab-row-between"><h3>{text.title}</h3>
      {typeof header === "string" ? <span className="cab-desc">{header}</span> : <Badge>{text.once}</Badge>}</div>}
    <div className={`cab-code${left ? "" : " cab-code-expired"}`} aria-label={code.split("").join(" ")}>{code}</div>
    {left
      ? <div className="cab-timer"><span role="timer" aria-live="off">{text.expiresIn(clock(left))}</span>
          <Meter className="cab-timer-meter" tone="info" value={left} max={PAIRING_TTL} aria-label={text.expiresIn(clock(left))} /></div>
      : <div className="cab-timer"><Badge tone="warning">{text.expired}</Badge>
          <Button size="sm" variant="secondary" loading={renewing} onClick={onRenew}><Icon name="refresh" />{text.renew}</Button></div>}
    {text.hint && <p className="cab-desc cab-small">{text.hint}</p>}
  </div>;
}
