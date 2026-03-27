import { getConfig } from "./modules/config.js";
import { setupPauseScreen } from "./modules/pauseModul.js";
import { initAvatarSystem } from "./modules/userAvatar.js";
import { setupHoverForAllItems } from "./modules/hoverTrailerModal.js";
import { initUserProfileAvatarPicker } from "./modules/avatarPicker.js";
import { initProfileChooser, syncProfileChooserHeaderButtonVisibility } from "./modules/profileChooser.js";
import { ensureStudioHubsMounted } from "./modules/studioHubs.js";

const idle = window.requestIdleCallback || ((cb) => setTimeout(cb, 0));
const LITE_ONLY = true;

function isVisible(el) {
  if (!el) return false;
  if (el.classList?.contains("hide")) return false;
  const rect = el.getBoundingClientRect?.();
  return !!rect && rect.width >= 1 && rect.height >= 1;
}

export function waitForAnyVisible(selectors, { timeout = 20000 } = {}) {
  return new Promise((resolve) => {
    const check = () => {
      for (const sel of selectors || []) {
        const el = document.querySelector(sel);
        if (el && isVisible(el)) {
          cleanup();
          resolve(el);
          return true;
        }
      }
      return false;
    };

    const mo = new MutationObserver(() => check());
    mo.observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true
    });

    const to = setTimeout(() => {
      cleanup();
      resolve(null);
    }, timeout);

    function cleanup() {
      clearTimeout(to);
      mo.disconnect();
    }

    if (check()) return;
  });
}

export function isMobileDevice() {
  try {
    const coarse = window.matchMedia?.("(pointer: coarse)")?.matches === true;
    const small = window.matchMedia?.("(max-width: 900px)")?.matches === true;
    const uaMobile = /Android|iPhone|iPad|iPod|IEMobile|Opera Mini/i.test(navigator.userAgent);
    return coarse || (small && uaMobile);
  } catch {
    return false;
  }
}

export function loadCSS(href, id = "") {
  const key = id || href;
  if (!key) return null;

  const existing = id
    ? document.getElementById(id)
    : document.querySelector(`link[rel=\"stylesheet\"][href=\"${href}\"]`);
  if (existing) return existing;

  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = href;
  if (id) link.id = id;
  (document.head || document.documentElement).appendChild(link);
  return link;
}

function applyLiteFeatureSelection(baseConfig) {
  if (!LITE_ONLY) return baseConfig || {};

  const cfg = { ...(baseConfig || {}) };

  cfg.enableSlider = false;
  cfg.enableNotifications = false;
  cfg.enableDirectorRows = false;
  cfg.enableRecentRows = false;
  cfg.enablePersonalRecommendations = false;
  cfg.enableGenreHubs = false;
  cfg.enableContinueMovies = false;
  cfg.enableContinueSeries = false;
  cfg.enableRecentMoviesRow = false;
  cfg.enableRecentSeriesRow = false;
  cfg.enableRecentMusicRow = false;
  cfg.enableRecentMusicTracksRow = false;
  cfg.enableRecentEpisodesRow = false;
  cfg.enableBecauseYouWatched = false;
  cfg.enabledGmmp = false;
  cfg.enableSubtitleCustomizer = false;
  cfg.enableQualityBadges = false;

  cfg.enableProfileChooser = true;
  cfg.createAvatar = true;
  cfg.previewModal = true;
  cfg.allPreviewModal = true;
  cfg.globalPreviewMode = "modal";
  cfg.preferTrailersInPreviewModal = true;
  cfg.onlyTrailerInPreviewModal = false;
  cfg.disableAllPlayback = false;
  cfg.enableTrailerThenVideo = true;
  cfg.enableStudioHubs = true;

  cfg.pauseOverlay = {
    ...(cfg.pauseOverlay || {}),
    showAgeBadge: true
  };

  return cfg;
}

const config = applyLiteFeatureSelection(getConfig());
if (LITE_ONLY) {
  window.__JMS_GLOBAL_CONFIG__ = config;
}

function removeUnsupportedSections() {
  const selectors = [
    "#slides-container",
    "#recent-rows",
    "#genre-hubs",
    "#personal-recommendations",
    ".personal-recs-section",
    ".director-rows-wrapper",
    ".recent-row-section"
  ];

  const nodes = document.querySelectorAll(selectors.join(","));
  nodes.forEach((el) => {
    if (el.closest("#studio-hubs")) return;
    if (el.closest("#itemDetailPage")) return;
    el.remove();
  });
}

function bootLiteModules() {
  syncProfileChooserHeaderButtonVisibility(config?.enableProfileChooser !== false);

  try {
    if (!window.cleanupPauseOverlay) {
      window.cleanupPauseOverlay = setupPauseScreen();
    }
  } catch {}

  try {
    if (!window.cleanupAvatarPicker) {
      window.cleanupAvatarPicker = initUserProfileAvatarPicker();
    }
  } catch {}

  try {
    if (!window.cleanupAvatarSystem) {
      window.cleanupAvatarSystem = initAvatarSystem();
    }
  } catch {}

  try {
    if (!window.cleanupProfileChooser) {
      window.cleanupProfileChooser = initProfileChooser();
    }
  } catch {}

  try {
    setupHoverForAllItems();
  } catch {}

  removeUnsupportedSections();

  idle(() => {
    try {
      ensureStudioHubsMounted({ eager: true });
    } catch {}
  });
}

function mountObservers() {
  const rerun = () => {
    try {
      setupHoverForAllItems();
    } catch {}
    try {
      ensureStudioHubsMounted();
    } catch {}
    removeUnsupportedSections();
  };

  const mo = new MutationObserver(() => rerun());
  mo.observe(document.documentElement, {
    childList: true,
    subtree: true,
    attributes: true
  });

  window.addEventListener("pageshow", rerun);
  window.addEventListener("hashchange", rerun);
  window.addEventListener("popstate", rerun);
}

bootLiteModules();
mountObservers();
