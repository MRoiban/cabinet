import type { PairingText } from "../components/PairingCode";

export type Lang = "en" | "fr";
export const defaultLang = (): Lang => navigator.language.toLowerCase().startsWith("fr") ? "fr" : "en";

const en = {
  hi: (name: string) => `Hi ${name}`,
  intro: "Your saves, achievements and playtime follow you between your PCs and your Steam Deck.",
  connect: "Connect a device",
  deck: "Steam Deck", deckWhere: "LuaTools sidebar or Lumen", getCode: "Get a pairing code", codeFailed: "Couldn't get a pairing code. Try again.",
  steps: ["Press … and open LuaTools Fixes", "Type the code under Cloud saves", "Restart Steam when asked"],
  pc: "Windows PC", pcWhere: "Cabinet or LuaTools", open: "Open in Cabinet", linkLabel: "Connect link", copyLink: "Copy link",
  copied: "Link copied", copyFailed: "Copy failed. Select the link and copy it.",
  installBefore: "No Cabinet yet? Run", installAfter: "in PowerShell.",
  games: "Your games", gameCount: (n: number) => `${n} ${n === 1 ? "game" : "games"}`,
  achievements: (n: number) => `${n} ${n === 1 ? "achievement" : "achievements"}`,
  saveFrom: (device: string, when: string) => `Save from ${device} · ${when}`, steamCloud: "Steam Cloud",
  noGames: "No games yet", noGamesHint: "Play a game on a connected device. Its saves, achievements and playtime will show up here.",
  storage: "Steam Cloud storage", of: (used: string, quota: string) => `${used} of ${quota}`,
  files: (n: string) => `${n} files kept for your Linux devices.`,
  invalid: "This link doesn’t work", invalidHint: "It may be incomplete or revoked. Ask the person who runs this server for a new connect link or pairing code.",
  offline: "Couldn’t reach Cabinet Cloud", offlineHint: "Check your connection, then try again.", retry: "Try again",
  now: "Now", never: "Never", language: "Language",
  pairing: { title: "Pairing code", once: "Works once", expiresIn: (t: string) => `Expires in ${t}`, expired: "Expired", renew: "Get a new code" } as PairingText,
};
export type Strings = typeof en;

// FR copy still needs a native-speaker pass.
const fr: Strings = {
  hi: name => `Bonjour ${name}`,
  intro: "Vos sauvegardes, succès et temps de jeu vous suivent entre vos PC et votre Steam Deck.",
  connect: "Connecter un appareil",
  deck: "Steam Deck", deckWhere: "Barre latérale LuaTools ou Lumen", getCode: "Obtenir un code d’association", codeFailed: "Impossible d’obtenir un code. Réessayez.",
  steps: ["Appuyez sur … et ouvrez LuaTools Fixes", "Saisissez le code sous Cloud saves", "Redémarrez Steam quand c’est demandé"],
  pc: "PC Windows", pcWhere: "Cabinet ou LuaTools", open: "Ouvrir dans Cabinet", linkLabel: "Lien de connexion", copyLink: "Copier le lien",
  copied: "Lien copié", copyFailed: "Échec de la copie. Sélectionnez le lien et copiez-le.",
  installBefore: "Pas encore Cabinet ? Lancez", installAfter: "dans PowerShell.",
  games: "Vos jeux", gameCount: n => `${n} ${n === 1 ? "jeu" : "jeux"}`,
  achievements: n => `${n} succès`,
  saveFrom: (device, when) => `Sauvegarde de ${device} · ${when}`, steamCloud: "Steam Cloud",
  noGames: "Aucun jeu pour l’instant", noGamesHint: "Jouez sur un appareil connecté. Ses sauvegardes, succès et temps de jeu apparaîtront ici.",
  storage: "Stockage Steam Cloud", of: (used, quota) => `${used} sur ${quota}`,
  files: n => `${n} fichiers conservés pour vos appareils Linux.`,
  invalid: "Ce lien ne fonctionne pas", invalidHint: "Il est peut-être incomplet ou révoqué. Demandez un nouveau lien de connexion ou un code d’association à la personne qui gère ce serveur.",
  offline: "Impossible de joindre Cabinet Cloud", offlineHint: "Vérifiez votre connexion, puis réessayez.", retry: "Réessayer",
  now: "Maintenant", never: "Jamais", language: "Langue",
  pairing: { title: "Code d’association", once: "Usage unique", expiresIn: t => `Expire dans ${t}`, expired: "Expiré", renew: "Obtenir un nouveau code" },
};
export const strings: Record<Lang, Strings> = { en, fr };
