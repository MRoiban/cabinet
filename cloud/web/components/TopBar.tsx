import type { ReactNode } from "react";
import { Icon } from "@bedrock/ui";

export function TopBar({ children }: { children?: ReactNode }) {
  return <header className="cab-topbar">
    <a className="cab-brand" href={location.pathname.startsWith("/admin") ? "/admin" : location.href}>
      <span className="cab-logo" aria-hidden="true"><Icon name="cloud" /></span>Cabinet Cloud
    </a>
    <span className="cab-spacer" />
    {children}
  </header>;
}
