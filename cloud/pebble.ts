import { definePebble } from "bedrock";
import { users, tokens, apps, devices, saves, presence, cloudFiles, pairingCodes, appNames } from "./schema";
import { saveZips, cloudBlobs } from "./storage";
import { apiRoutes } from "./api";
import { cloudRoutes } from "./cloud";
import { pairingRoutes } from "./pairing";
import { nameJobs, nameRoutes } from "./names";
import { adminMutations, adminQueries } from "./admin";

// The web app serves every other path, including /admin and /connect#<token>.
export default definePebble({
  name: "cabinet", access: "public", web: "./web/index.html",
  schema: { users, tokens, apps, devices, saves, presence, cloudFiles, pairingCodes, appNames }, storage: [saveZips, cloudBlobs],
  queries: adminQueries, mutations: adminMutations, jobs: nameJobs,
  routes: { ...apiRoutes, ...cloudRoutes, ...pairingRoutes, ...nameRoutes },
});
