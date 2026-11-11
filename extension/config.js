// Local Test : l'API sert aussi l'extension (via le tunnel, voir README), donc meme origine.
// shortcut: faux en hosted test (pages sur le CDN Twitch), y mettre alors l'URL absolue de l'EBS.
window.HITMAP_EBS_URL = location.origin;
