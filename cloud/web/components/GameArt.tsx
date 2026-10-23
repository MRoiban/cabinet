import { useState } from "react";
import { Icon } from "@bedrock/ui";
import { artUrl } from "../lib/format";

// Steam header art (460:215); a neutral tile stands in when the CDN has none.
export function GameArt({ appid, className = "" }: { appid: string; className?: string }) {
  const [failed, setFailed] = useState(false);
  return failed
    ? <span className={`cab-art cab-art-missing ${className}`} aria-hidden="true"><Icon name="game" /></span>
    : <img className={`cab-art ${className}`} src={artUrl(appid)} alt="" loading="lazy" onError={() => setFailed(true)} />;
}
