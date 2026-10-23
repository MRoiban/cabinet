import { definePebble } from "bedrock";
import { users, tokens, apps, devices, saves, presence, cloudFiles } from "./schema";
import { saveZips, cloudBlobs } from "./storage";
import { apiRoutes } from "./api";
import { cloudRoutes } from "./cloud";
import { adminRoutes } from "./admin";
import { connectPage } from "./pages";

export default definePebble({
  name: "cabinet", access: "public",
  schema: { users, tokens, apps, devices, saves, presence, cloudFiles }, storage: [saveZips, cloudBlobs],
  routes: {
    ...apiRoutes, ...cloudRoutes, ...adminRoutes,
    "GET /connect": () => connectPage(),
    "GET /": () => new Response(null, { status: 302, headers: { location: "/connect" } }),
  },
});
