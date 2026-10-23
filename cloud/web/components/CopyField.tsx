import { Icon, InputGroup, InputGroupButton, InputGroupInput, useToast } from "@bedrock/ui";

export async function copyText(text: string): Promise<boolean> {
  try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}
export function CopyField({ value, label, copied = "Copied", failed = "Copy failed. Select the text and copy it." }:
  { value: string; label: string; copied?: string; failed?: string }) {
  const { toast } = useToast();
  async function copy() {
    const ok = await copyText(value);
    toast(ok ? { title: copied, tone: "success", duration: 2500 } : { title: failed, tone: "danger" });
  }
  return <InputGroup className="cab-copy" trailing={<InputGroupButton aria-label={label} onClick={() => void copy()}><Icon name="copy" /></InputGroupButton>}>
    <InputGroupInput className="cab-mono" readOnly value={value} aria-label={label} onFocus={event => event.currentTarget.select()} />
  </InputGroup>;
}
