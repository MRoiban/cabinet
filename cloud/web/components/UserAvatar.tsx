import type { CSSProperties } from "react";
import { Avatar } from "@bedrock/ui";
import { avatarColor } from "../lib/format";

export function UserAvatar({ name, seed, muted = false, size = "md" }: { name: string; seed: string; muted?: boolean; size?: "sm" | "md" | "lg" }) {
  return <Avatar className="cab-avatar" name={name} size={size} aria-hidden="true"
    style={{ "--cab-avatar": avatarColor(seed, muted) } as CSSProperties} />;
}
