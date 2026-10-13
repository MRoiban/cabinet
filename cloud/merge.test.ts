import { expect, test } from "bun:test";
import { emptyState, merge, progress } from "./merge";

const push = (deviceId: string, playtimeMinutes: number) => ({ deviceId, deviceName: deviceId, achievements: {}, playtimeMinutes });
test("achievements union and earliest time survive reverse-order pushes", () => {
  let state = merge(emptyState(), { ...push("a", 5), achievements: { FIRST: 200, SECOND: 300 } });
  state = merge(state, { ...push("b", 3), achievements: { FIRST: 100, THIRD: 400 } });
  state = merge(state, { ...push("a", 1), achievements: { FIRST: 500 } });
  expect(state.achievements).toEqual({ FIRST: 100, SECOND: 300, THIRD: 400 });
});
test("stats replace the whole map only when strictly newer", () => {
  let state = merge(emptyState(), { ...push("a", 0), stats: { old: 3 }, statsUpdatedAt: 100 });
  for (const time of [50, 100]) state = merge(state, { ...push("b", 0), stats: { ignored: 99 }, statsUpdatedAt: time });
  expect(state.stats).toEqual({ old: 3 });
  state = merge(state, { ...push("a", 0), stats: { next: -2.5 }, statsUpdatedAt: 101 });
  expect(state.stats).toEqual({ next: -2.5 });
  expect(state.statsUpdatedAt).toBe(101);
});
test("playtime uses device max and sums devices, including hostile object keys", () => {
  let state = emptyState();
  for (const [id, minutes] of [["a", 30], ["b", 40], ["a", 20], ["a", 35], ["__proto__", 2]] as const) state = merge(state, push(id, minutes));
  expect(state.playtime).toEqual(JSON.parse('{"a":35,"b":40,"__proto__":2}'));
  expect(state.playtimeTotal).toBe(77);
});
test("rejects invalid shapes, timestamps, stats pairs and nonfinite values", () => {
  for (const value of [[], { ...push("a", -1) }, { ...push("a", 0), achievements: [] },
    { ...push("a", 0), achievements: { A: -1 } }, { ...push("a", 0), stats: {} },
    { ...push("a", 0), statsUpdatedAt: 1 }, { ...push("a", 0), stats: { x: Infinity }, statsUpdatedAt: 1 }]) {
    expect(() => progress(value)).toThrow();
  }
});
