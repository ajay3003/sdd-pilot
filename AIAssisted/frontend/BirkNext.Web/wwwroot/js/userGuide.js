window.userGuide = {
    // Marks the guide navigation link of the topic that owns an anchor (aria-current), so the current section is highlighted.
    markCurrent(topicId) {
        if (!topicId) {
            return;
        }

        document.querySelectorAll(".ug-nav a[aria-current]").forEach(link => link.removeAttribute("aria-current"));
        const link = document.querySelector(`.ug-nav a[data-topic="${CSS.escape(topicId)}"]`);
        if (link) {
            link.setAttribute("aria-current", "location");
        }
    },

    topicOf(element) {
        return element?.closest("[data-topic]")?.getAttribute("data-topic") ?? null;
    },

    scrollToSection(sectionId) {
        const scroll = () => {
            const section = document.getElementById(sectionId);
            if (!section) {
                return;
            }

            // Instant: the app's smooth scrolling would let the scroll spy mark every section it passes.
            section.scrollIntoView({ block: "start", behavior: "instant" });
        };

        scroll();
        requestAnimationFrame(scroll);
        window.setTimeout(scroll, 150);
        window.setTimeout(scroll, 500);
        history.replaceState(null, "", `/user-guide#${sectionId}`);

        // Keyboard and screen-reader users land on the target: a section moves focus to its heading, any other anchor to itself.
        const target = document.getElementById(sectionId);
        if (target) {
            const focusTarget = target.matches("section") ? target.querySelector(".ug-section-title") ?? target : target;
            if (!focusTarget.hasAttribute("tabindex") && !focusTarget.matches("a, button, input, select, textarea")) {
                focusTarget.setAttribute("tabindex", "-1");
            }
            focusTarget.focus({ preventScroll: true });
            window.userGuide.chosen = { topic: window.userGuide.topicOf(target), at: Date.now() };
            window.userGuide.markCurrent(window.userGuide.chosen.topic);
        }
    },

    // Scroll spy: the last topic whose top has passed the upper part of the viewport is the current one.
    syncWithScroll() {
        if (window.location.pathname !== "/user-guide") {
            return;
        }

        const topics = Array.from(document.querySelectorAll("section.ug-topic[data-topic]"));
        if (topics.length === 0) {
            return;
        }

        // A section the reader just chose stays current while its scroll settles, and at the end of the page (where the last short
        // sections can never reach the top).
        const chosen = window.userGuide.chosen;
        const atEnd = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 2;
        if (chosen && (Date.now() - chosen.at < 800 || atEnd)) {
            window.userGuide.markCurrent(chosen.topic);
            return;
        }

        let current = null;
        for (const topic of topics) {
            if (topic.getBoundingClientRect().top <= 140) {
                current = topic;
            }
        }
        window.userGuide.markCurrent(current?.getAttribute("data-topic") ?? null);
    }
};

document.addEventListener("click", (event) => {
    const link = event.target.closest('a[href^="/user-guide#"]');
    if (!link) {
        return;
    }

    const url = new URL(link.href);
    if (url.origin !== window.location.origin || url.pathname !== "/user-guide" || window.location.pathname !== "/user-guide") {
        return;
    }

    event.preventDefault();
    window.userGuide.scrollToSection(url.hash.slice(1));
});

let userGuideScrollFrame = 0;
window.addEventListener("scroll", () => {
    if (userGuideScrollFrame) {
        return;
    }

    userGuideScrollFrame = requestAnimationFrame(() => {
        userGuideScrollFrame = 0;
        window.userGuide.syncWithScroll();
    });
}, { passive: true, capture: true });

window.addEventListener("hashchange", () => {
    if (window.location.pathname === "/user-guide" && window.location.hash.length > 1) {
        window.userGuide.scrollToSection(window.location.hash.slice(1));
    }
});

window.addEventListener("load", () => {
    if (window.location.pathname === "/user-guide" && window.location.hash.length > 1) {
        window.userGuide.scrollToSection(window.location.hash.slice(1));
    }
});
