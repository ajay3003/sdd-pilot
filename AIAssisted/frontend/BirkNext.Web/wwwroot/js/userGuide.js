// Help Center (Pages/UserGuide.razor). The page decides which article is shown from the URL fragment; this script only:
// - turns same-page /user-guide#anchor links into history entries (pushState) and tells the page (so Back/Forward work),
// - scrolls to and focuses the target, without touching history,
// - gives the page the headings of an article ("On this page") and the article that owns a heading anchor.
window.userGuide = {
    page: null,

    register(dotnet) {
        window.userGuide.page = dotnet;
    },

    topicOf(element) {
        return element?.closest("[data-topic]")?.getAttribute("data-topic") ?? null;
    },

    /** The article (data-topic) that contains an element with this id, or null when no such element exists. */
    topicOfAnchor(anchor) {
        const element = document.getElementById(anchor);
        return element ? window.userGuide.topicOf(element) : null;
    },

    /** The h3 headings of one article, each with a stable id (an existing id, or topic + heading text). */
    headings(topicId) {
        const section = document.querySelector(`section.ug-topic[data-topic="${CSS.escape(topicId)}"]`);
        if (!section) {
            return [];
        }

        const used = new Set();
        return Array.from(section.querySelectorAll("h3")).map(h => {
            if (!h.id) {
                const slug = (h.textContent || "").toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 60) || "section";
                let id = `${topicId}--${slug}`;
                for (let n = 2; used.has(id) || (document.getElementById(id) && document.getElementById(id) !== h); n++) {
                    id = `${topicId}--${slug}-${n}`;
                }
                h.id = id;
            }
            used.add(h.id);
            h.classList.add("ug-anchor-target");
            return { id: h.id, text: (h.textContent || "").trim() };
        }).filter(h => h.text.length > 0);
    },

    scrollToSection(sectionId) {
        const scroll = () => {
            const target = document.getElementById(sectionId);
            if (!target) {
                return;
            }
            // A whole article starts at the top of the page (breadcrumb and title visible); a heading inside it is scrolled into view
            // below the app's top bar (scroll-margin-top in user-guide.css).
            if (target.matches("section.ug-topic")) {
                window.scrollTo({ top: 0, behavior: "instant" });
                document.querySelector(".content, main, article.content")?.scrollTo?.({ top: 0, behavior: "instant" });
            } else {
                target.scrollIntoView({ block: "start", behavior: "instant" });
            }
        };

        scroll();
        requestAnimationFrame(scroll);
        window.setTimeout(scroll, 150);

        // Keyboard and screen-reader users land on the target: an article moves focus to its heading, any other anchor to itself.
        const target = document.getElementById(sectionId);
        if (target) {
            const focusTarget = target.matches("section") ? target.querySelector(".ug-section-title") ?? target : target;
            if (!focusTarget.hasAttribute("tabindex") && !focusTarget.matches("a, button, input, select, textarea")) {
                focusTarget.setAttribute("tabindex", "-1");
            }
            focusTarget.focus({ preventScroll: true });
        }
    }
};

// Capture phase: runs before Blazor's own link handling, so a same-page fragment link becomes exactly one history entry.
document.addEventListener("click", (event) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
        return;
    }

    const link = event.target.closest('a[href*="user-guide#"]');
    if (!link || !window.userGuide.page || window.location.pathname !== "/user-guide") {
        return;
    }

    const url = new URL(link.href);
    if (url.origin !== window.location.origin || url.pathname !== "/user-guide") {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    if (url.hash !== window.location.hash) {
        history.pushState(null, "", `/user-guide${url.hash}`);
    }
    window.userGuide.page.invokeMethodAsync("ShowAnchor", decodeURIComponent(url.hash.slice(1)));
}, true);

window.addEventListener("popstate", () => {
    if (window.location.pathname === "/user-guide" && window.userGuide.page) {
        window.userGuide.page.invokeMethodAsync("ShowAnchor", decodeURIComponent(window.location.hash.slice(1)));
    }
});
