import { definePebble } from "bedrock";
import { users, tokens, apps, devices, saves, presence } from "./schema";
import { saveZips } from "./storage";
import { apiRoutes } from "./api";
import { adminRoutes } from "./admin";
import { connectPage } from "./pages";

export default definePebble({
  name: "cabinet", access: "public",
  schema: { users, tokens, apps, devices, saves, presence }, storage: [saveZips],
  routes: {
    ...apiRoutes, ...adminRoutes,
    "GET /connect": () => connectPage(),
    "GET /": () => new Response(null, { status: 302, headers: { location: "/connect" } }),
  },
});
