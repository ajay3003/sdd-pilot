window.userGuide = {
    scrollToSection(sectionId) {
        const scroll = () => {
            const section = document.getElementById(sectionId);
            if (!section) {
                return;
            }

            section.scrollIntoView({ block: "start", behavior: "auto" });
        };

        scroll();
        requestAnimationFrame(scroll);
        window.setTimeout(scroll, 150);
        window.setTimeout(scroll, 500);
        history.replaceState(null, "", `/user-guide#${sectionId}`);
    }
};

document.addEventListener("click", (event) => {
    const link = event.target.closest('a[href^="/user-guide#"]');
    if (!link) {
        return;
    }

    const url = new URL(link.href);
    if (url.origin !== window.location.origin || url.pathname !== "/user-guide") {
        return;
    }

    event.preventDefault();
    window.userGuide.scrollToSection(url.hash.slice(1));
});

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
