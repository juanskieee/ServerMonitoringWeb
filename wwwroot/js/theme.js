(() => {
    const STORAGE_KEY = "server-monitor-theme";

    applySavedTheme();

    document.addEventListener(
        "DOMContentLoaded",
        initializeThemeSelector
    );

    function applySavedTheme() {
        document.documentElement.dataset.theme =
            loadTheme();
    }

    function initializeThemeSelector() {
        const select =
            document.getElementById("theme-select");

        if (!select)
            return;

        const currentTheme =
            document.documentElement.dataset.theme;

        select.value = currentTheme;
        select.disabled = false;

        select.addEventListener(
            "change",
            handleThemeChange
        );
    }

    function handleThemeChange(event) {
        const theme =
            event.target.value;

        document.documentElement.dataset.theme =
            theme;

        saveTheme(theme);
    }

    function loadTheme() {
        try {
            return localStorage.getItem(STORAGE_KEY) === "light"
                ? "light"
                : "dark";
        }
        catch {
            return "dark";
        }
    }

    function saveTheme(theme) {
        try {
            localStorage.setItem(
                STORAGE_KEY,
                theme
            );
        }
        catch {
            // Theme still works for this session.
        }
    }
})();