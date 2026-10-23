import { createRoot } from "react-dom/client";
import { ToastProvider, TooltipProvider } from "@bedrock/ui";
import "@bedrock/ui/styles.css";
import "./styles.css";
import { Admin } from "./admin/Admin";
import { Connect } from "./connect/Connect";

// /admin is the creator console; every other path is the connect page (/connect#cab_…).
const admin = location.pathname === "/admin" || location.pathname.startsWith("/admin/");
createRoot(document.getElementById("root")!).render(
  <TooltipProvider><ToastProvider>{admin ? <Admin /> : <Connect />}</ToastProvider></TooltipProvider>,
);
