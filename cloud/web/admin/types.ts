import type { ClientResult } from "bedrock/client";
import type pebble from "../../pebble";

type Queries = NonNullable<typeof pebble["queries"]>;
type Mutations = NonNullable<typeof pebble["mutations"]>;
export type Pebble = typeof pebble;
export type Overview = ClientResult<Queries["adminOverview"]>;
export type UserRow = Overview["users"][number];
export type UserDetail = ClientResult<Queries["adminUser"]>;
export type Created = ClientResult<Mutations["createUser"]>;
export type HubcapView = ClientResult<Queries["adminHubcap"]>;
export const linkFor = (path: string) => new URL(path, location.origin).href;
