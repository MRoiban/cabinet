import { definePebble } from "bedrock";
import { users, tokens, apps, devices, saves, presence, cloudFiles, pairingCodes, appNames, saveArchives } from "./schema";
import { saveZips, cloudBlobs, archiveZips } from "./storage";
import { apiRoutes } from "./api";
import { archiveRoutes } from "./archive";
import { cloudRoutes } from "./cloud";
import { pairingRoutes } from "./pairing";
import { nameJobs, nameRoutes } from "./names";
import { adminMutations, adminQueries } from "./admin";

// The web app serves every other path, including /admin and /connect#<token>.
export default definePebble({
  name: "cabinet", access: "public", web: "./web/index.html",
  schema: { users, tokens, apps, devices, saves, presence, cloudFiles, pairingCodes, appNames, saveArchives }, storage: [saveZips, cloudBlobs, archiveZips],
  queries: adminQueries, mutations: adminMutations, jobs: nameJobs,
  routes: { ...apiRoutes, ...archiveRoutes, ...cloudRoutes, ...pairingRoutes, ...nameRoutes },
});
