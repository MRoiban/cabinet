import { definePebble } from "bedrock";
import { users, tokens, apps, devices, saves, presence, cloudFiles, pairingCodes, appNames } from "./schema";
import { saveZips, cloudBlobs } from "./storage";
import { apiRoutes } from "./api";
import { cloudRoutes } from "./cloud";
import { pairingRoutes } from "./pairing";
import { nameJobs, nameRoutes } from "./names";
import { adminRoutes } from "./admin";
import { connectPage } from "./pages";

export default definePebble({
  name: "cabinet", access: "public",
  schema: { users, tokens, apps, devices, saves, presence, cloudFiles, pairingCodes, appNames }, storage: [saveZips, cloudBlobs], jobs: nameJobs,
  routes: {
    ...apiRoutes, ...cloudRoutes, ...pairingRoutes, ...nameRoutes, ...adminRoutes,
    "GET /connect": () => connectPage(),
    "GET /": () => new Response(null, { status: 302, headers: { location: "/connect" } }),
  },
});
