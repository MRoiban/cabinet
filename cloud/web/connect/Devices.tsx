import { useState } from "react";
import { Button, Card, Icon, Kbd, useToast } from "@bedrock/ui";
import { CopyField } from "../components/CopyField";
import { PairingCode } from "../components/PairingCode";
import type { Strings } from "../lib/i18n";
import { bearer } from "./api";

const INSTALL = "irm https://raw.githubusercontent.com/MRoiban/cabinet/main/install.ps1 | iex";

export function Devices({ token, t }: { token: string; t: Strings }) {
  const { toast } = useToast();
  const [code, setCode] = useState<{ code: string; expiresAt: number } | null>(null);
  const [pending, setPending] = useState(false);
  const link = `${location.origin}/connect#${token}`;
  const open = `cabinet://connect?server=${encodeURIComponent(location.origin)}&token=${encodeURIComponent(token)}`;
  async function getCode() {
    setPending(true);
    try { setCode(await bearer(token)<{ code: string; expiresAt: number }>("/api/pairing-codes", { method: "POST" })); }
    catch { toast({ title: t.codeFailed, tone: "danger" }); }
    finally { setPending(false); }
  }
  const [first, second, third] = t.steps;
  return <section className="cab-section">
    <h2 className="cab-section-title">{t.connect}</h2>
    <div className="cab-devices">
      <Card className="cab-device">
        <DeviceHead icon="device-mobile" title={t.deck} where={t.deckWhere} />
        {code
          ? <PairingCode compact header={false} text={t.pairing} {...code} renewing={pending} onRenew={() => void getCode()} />
          : <Button className="cab-block" loading={pending} onClick={() => void getCode()}><Icon name="key" />{t.getCode}</Button>}
        <ol className="cab-steps">
          <li>{first!.split("…")[0]}<Kbd>…</Kbd>{first!.split("…")[1]}</li><li>{second}</li><li>{third}</li>
        </ol>
      </Card>
      <Card className="cab-device">
        <DeviceHead icon="device-desktop" title={t.pc} where={t.pcWhere} />
        <Button asChild className="cab-block"><a href={open}><Icon name="link" />{t.open}</a></Button>
        <CopyField value={link} label={t.copyLink} copied={t.copied} failed={t.copyFailed} />
        <p className="cab-desc cab-small">{t.installBefore} <code className="cab-mono cab-inline-code">{INSTALL}</code> {t.installAfter}</p>
      </Card>
    </div>
  </section>;
}
function DeviceHead({ icon, title, where }: { icon: string; title: string; where: string }) {
  return <div className="cab-device-head">
    <span className="cab-device-icon" aria-hidden="true"><Icon name={icon} /></span>
    <div><h3>{title}</h3><p className="cab-desc cab-small">{where}</p></div>
  </div>;
}
