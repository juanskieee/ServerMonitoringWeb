(() => {
  const REFRESH_INTERVAL_MS = 3000;
  const REFRESH_TIMEOUT_MS = 8000;
  const RING_ANIMATION_DURATION_MS = 850;
  const RING_ANIMATION_EASING = "cubic-bezier(0.22, 1, 0.36, 1)";
  const EMPTY_RING_DASH_OFFSET = "100";

  // These coordinates match the 160-by-160 SVG in _UsageDonut.cshtml.
  const DONUT_VIEWBOX_SIZE = 160;
  const DONUT_CENTER = DONUT_VIEWBOX_SIZE / 2;
  const GUIDE_VERTICAL_STEP = 32;
  const GUIDE_VALUE_GAP_PX = 4;
  const GUIDE_RING_Y = {
    cpu: DONUT_CENTER - GUIDE_VERTICAL_STEP,
    ram: DONUT_CENTER,
    disk: DONUT_CENTER + GUIDE_VERTICAL_STEP,
  };

  const dashboardContent = document.getElementById("dashboard-content");
  const refreshStatus = document.getElementById("refresh-state");
  if (!dashboardContent || !refreshStatus) {
    return;
  }

  const warningSound = document.getElementById("server-warning-sound");
  const reducedMotionPreference = window.matchMedia(
    "(prefers-reduced-motion: reduce)",
  );

  let isRefreshing = false;
  let refreshTimerId;
  let isStartingWarningSound = false;

  function getUsageState(percent) {
    if (!Number.isFinite(percent)) {
      return "";
    }

    if (percent >= 95) {
      return "gauge-critical";
    }

    if (percent >= 85) {
      return "gauge-high";
    }

    if (percent >= 70) {
      return "gauge-warn";
    }

    if (percent >= 55) {
      return "gauge-elevated";
    }

    return "gauge-good";
  }

  function parseUsagePercent(metricValueElement) {
    return Number.parseFloat(metricValueElement.textContent);
  }

  function updateGaugeColors(cardElement) {
    const metrics = ["cpu", "ram", "disk"];

    for (const metric of metrics) {
      const metricGroup = cardElement.querySelector(
        `[data-metric="${metric}"]`,
      );

      const metricValue = cardElement.querySelector(
        `[data-metric-value="${metric}"]`,
      );

      if (!metricGroup || !metricValue) {
        continue;
      }

      metricGroup.classList.remove(
        "gauge-good",
        "gauge-elevated",
        "gauge-warn",
        "gauge-high",
        "gauge-critical",
      );

      const percent = parseUsagePercent(metricValue);

      const stateClass = getUsageState(percent);

      if (stateClass) {
        metricGroup.classList.add(stateClass);
      }
    }

    for (const diskMeter of cardElement.querySelectorAll("meter.disk-bar")) {
      diskMeter.className = `disk-bar ${getUsageState(diskMeter.value)}`;
    }
  }

  function initializeDashboard() {
    document.addEventListener("click", updateWarningSound);
    document.addEventListener("keydown", updateWarningSound);

    const resizeObserver = new ResizeObserver(updateDonutGuides);
    resizeObserver.observe(dashboardContent);

    updateDonutGuides();
    animateInitialRings(dashboardContent);
    dashboardContent
      .querySelectorAll(".server-card")
      .forEach(updateGaugeColors);
    startPingLogCycles(dashboardContent);
    updateWarningSound();
    refreshTimerId = setTimeout(refreshDashboard, REFRESH_INTERVAL_MS);
  }

  function updateWarningSound() {
    if (!warningSound) {
      return;
    }

    const hasServerError = dashboardContent.querySelector(".card-error") !== null;
    if (!hasServerError) {
      warningSound.pause();
      warningSound.currentTime = 0;
      return;
    }

    startWarningSound();
  }

  function startWarningSound() {
    if (!warningSound.paused || isStartingWarningSound) {
      return;
    }

    isStartingWarningSound = true;
    warningSound
      .play()
      .catch(() => {
        // Autoplay may be blocked. Retry after a normal click or key press.
      })
      .finally(() => {
        isStartingWarningSound = false;
      });
  }

  function updateDonutGuides() {
    const donutFigures = dashboardContent.querySelectorAll(".resource-donut");
    for (const donutFigure of donutFigures) {
      positionDonutGuides(donutFigure);
    }
  }

  function positionDonutGuides(donutFigure) {
    const figureBounds = donutFigure.getBoundingClientRect();
    const ringSvg = donutFigure.querySelector(".usage-rings");
    const chartBounds = ringSvg.getBoundingClientRect();
    const chartScale = chartBounds.width / DONUT_VIEWBOX_SIZE;
    const guidePaths = donutFigure.querySelectorAll("[data-guide]");

    for (const guidePath of guidePaths) {
      const metricName = guidePath.dataset.guide;
      const valueElement = donutFigure.querySelector(
        `[data-metric-value="${metricName}"]`,
      );
      const valueBounds = valueElement.getBoundingClientRect();
      const ringTrack = donutFigure.querySelector(
        `[data-metric="${metricName}"] .donut-track`,
      );
      const ringRadius = Number(ringTrack.getAttribute("r"));
      const ringPosition = getGuideRingPosition(metricName, ringRadius);

      // Convert SVG coordinates to pixels relative to the figure's guide overlay.
      const lineEndX = chartBounds.left - figureBounds.left + ringPosition.x * chartScale;
      const lineY = chartBounds.top - figureBounds.top + ringPosition.y * chartScale;
      const lineStartX = valueBounds.right - figureBounds.left + GUIDE_VALUE_GAP_PX;
      const lineStartY = valueBounds.top - figureBounds.top + valueBounds.height / 2;
      const lineElbowX = (lineStartX + lineEndX) / 2;

      const guidePathData =
        `M${lineStartX} ${lineStartY} H${lineElbowX} V${lineY} H${lineEndX}`;

      guidePath.setAttribute("d", guidePathData);
      const guideTip = donutFigure.querySelector(
        `[data-guide-tip="${metricName}"]`
      );

      if (guideTip) {
        guideTip.setAttribute("cx", lineEndX);
        guideTip.setAttribute("cy", lineY);
      }
    }
  }

  function getGuideRingPosition(metricName, ringRadius) {
    const ringY = GUIDE_RING_Y[metricName] ?? GUIDE_RING_Y.disk;

    // Circle equation: x^2 + y^2 = radius^2. Use its left edge at this height.
    const distanceFromCenterY = ringY - DONUT_CENTER;
    const distanceFromCenterX = Math.sqrt(
      ringRadius * ringRadius - distanceFromCenterY * distanceFromCenterY,
    );
    const ringX = DONUT_CENTER - distanceFromCenterX;
    return { x: ringX, y: ringY };
  }

  function animateInitialRings(rootElement) {
    if (reducedMotionPreference.matches) {
      return;
    }

    const ringFills = rootElement.querySelectorAll(".donut-fill");
    for (const ringFill of ringFills) {
      const isInvisible = Number(ringFill.style.opacity) === 0;
      if (isInvisible) {
        continue;
      }

      ringFill.animate(
        [
          { strokeDashoffset: EMPTY_RING_DASH_OFFSET, opacity: 0 },
          {
            strokeDashoffset: ringFill.style.strokeDashoffset,
            opacity: ringFill.style.opacity,
          },
        ],
        { duration: RING_ANIMATION_DURATION_MS, easing: RING_ANIMATION_EASING },
      );
    }
  }

  function startPingLogCycles(rootElement) {
    for (const pingLog of rootElement.querySelectorAll(".ping-log")) {
      // Leave the waiting message visible when there are no ping entries.
      if (pingLog.querySelector("time")) {
        pingLog.classList.add("ping-log-cycling");
      }
    }
  }

  function updateNotices(currentResponse, incomingResponse) {
    const currentNotices = currentResponse.querySelector("[data-notices]");
    const incomingNotices = incomingResponse.querySelector("[data-notices]");
    if (currentNotices.innerHTML !== incomingNotices.innerHTML) {
      currentNotices.innerHTML = incomingNotices.innerHTML;
    }
  }

  function updateServerCards(currentResponse, incomingResponse) {
    const serverGrid = currentResponse.querySelector(".server-grid");
    const incomingGrid = incomingResponse.querySelector(".server-grid");
    const existingCardsById = new Map();
    for (const serverCard of serverGrid.children) {
      existingCardsById.set(serverCard.dataset.serverId, serverCard);
    }

    // Snapshot this list because inserting new cards moves them out of the response.
    const incomingCards = Array.from(incomingGrid.children);
    for (let cardIndex = 0; cardIndex < incomingCards.length; cardIndex++) {
      const incomingCard = incomingCards[cardIndex];
      const serverId = incomingCard.dataset.serverId;
      let displayedCard = existingCardsById.get(serverId);

      if (displayedCard) {
        existingCardsById.delete(serverId);
        updateServerCardContent(displayedCard, incomingCard);
      } else {
        displayedCard = incomingCard;
      }

      const cardAtTargetIndex = serverGrid.children[cardIndex];
      if (cardAtTargetIndex !== displayedCard) {
        serverGrid.insertBefore(displayedCard, cardAtTargetIndex ?? null);
      }
      if (displayedCard === incomingCard) {
        updateGaugeColors(displayedCard);
        animateInitialRings(displayedCard);
      }
    }

    for (const removedCard of existingCardsById.values()) {
      removedCard.remove();
    }
  }

  function updateServerCardContent(displayedCard, incomingCard) {
    displayedCard.className = incomingCard.className;

    // Empty cards have no gauges or drives; online and error cards keep a ping log.
    if (displayedCard.querySelector(".card-empty-state") ||
        incomingCard.querySelector(".card-empty-state")) {
      if (displayedCard.innerHTML !== incomingCard.innerHTML) {
        displayedCard.innerHTML = incomingCard.innerHTML;
        updateGaugeColors(displayedCard);
        animateInitialRings(displayedCard);
      }
      return;
    }

    const currentHeading = displayedCard.querySelector("[data-card-heading]");
    const incomingHeading = incomingCard.querySelector("[data-card-heading]");
    if (currentHeading.innerHTML !== incomingHeading.innerHTML) {
      currentHeading.innerHTML = incomingHeading.innerHTML;
    }

    const currentDisks = displayedCard.querySelector(".disk-usage");
    const incomingDisks = incomingCard.querySelector(".disk-usage");
    if (currentDisks.innerHTML !== incomingDisks.innerHTML) {
      currentDisks.innerHTML = incomingDisks.innerHTML;
    }

    // Replacing only the log restarts its CSS cycle, even for an unchanged pair.
    // Removing the old list also ends its animation without any timer callbacks.
    const currentPingLog = displayedCard.querySelector(".ping-log");
    const incomingPingLog = incomingCard.querySelector(".ping-log");
    currentPingLog.replaceWith(incomingPingLog);

    updateUsageRings(displayedCard, incomingCard);
    updateUsageLabels(displayedCard, incomingCard);
    updateGaugeColors(displayedCard);
  }

  function updateUsageRings(displayedCard, incomingCard) {
    // Preserve the SVG nodes so CSS can transition from each ring's current value.
    const metricGroups = displayedCard.querySelectorAll("[data-metric]");
    for (const metricGroup of metricGroups) {
      const metricName = metricGroup.dataset.metric;
      const incomingGroup = incomingCard.querySelector(
        `[data-metric="${metricName}"]`,
      );
      metricGroup.setAttribute("class", incomingGroup.getAttribute("class"));

      const currentFill = metricGroup.querySelector(".donut-fill");
      const incomingFill = incomingGroup.querySelector(".donut-fill");
      currentFill.style.strokeDashoffset = incomingFill.style.strokeDashoffset;
      currentFill.style.opacity = incomingFill.style.opacity;
    }
  }

  function updateUsageLabels(displayedCard, incomingCard) {
    const metricValues = displayedCard.querySelectorAll("[data-metric-value]");
    for (const metricValue of metricValues) {
      const metricName = metricValue.dataset.metricValue;
      const incomingValue = incomingCard.querySelector(
        `[data-metric-value="${metricName}"]`,
      );
      if (metricValue.innerHTML !== incomingValue.innerHTML) {
        metricValue.innerHTML = incomingValue.innerHTML;
      }
    }

    const currentCenterLabel = displayedCard.querySelector(".donut-center");
    const incomingCenterLabel = incomingCard.querySelector(".donut-center");
    currentCenterLabel.textContent = incomingCenterLabel.textContent;
  }

  function updateDashboard(incomingResponse) {
    const currentResponse = dashboardContent.querySelector("[data-card-response]");
    updateNotices(currentResponse, incomingResponse);
    updateServerCards(currentResponse, incomingResponse);
    startPingLogCycles(dashboardContent);
    updateDonutGuides();
    updateWarningSound();
  }

  function parseDashboardResponse(responseHtml) {
    const responseDocument = new DOMParser().parseFromString(responseHtml, "text/html");
    const incomingResponse = responseDocument.querySelector("[data-card-response]");
    if (!incomingResponse || !incomingResponse.querySelector(".server-grid")) {
      throw new Error("Unexpected response");
    }

    return incomingResponse;
  }

  async function fetchDashboardResponse(abortSignal) {
    const response = await fetch(dashboardContent.dataset.refreshUrl, {
      cache: "no-store",
      signal: abortSignal,
      headers: { Accept: "text/html" },
    });
    if (!response.ok || response.redirected) {
      throw new Error("Refresh unavailable");
    }

    const responseHtml = await response.text();
    return parseDashboardResponse(responseHtml);
  }

  function showRefreshError() {
    const failureMessage = "Could not refresh. Retrying...";
    if (refreshStatus.textContent !== failureMessage) {
      refreshStatus.textContent = failureMessage;
    }
    refreshStatus.hidden = false;
  }

  function clearRefreshError() {
    refreshStatus.hidden = true;
    refreshStatus.textContent = "";
  }

  async function refreshDashboard() {
    if (isRefreshing) {
      return;
    }

    clearTimeout(refreshTimerId);
    isRefreshing = true;
    const refreshStartedAt = performance.now();
    dashboardContent.setAttribute("aria-busy", "true");

    const requestController = new AbortController();
    const requestTimeoutId = setTimeout(
      () => requestController.abort(),
      REFRESH_TIMEOUT_MS,
    );

    try {
      const incomingResponse = await fetchDashboardResponse(requestController.signal);
      updateDashboard(incomingResponse);
      clearRefreshError();
    } catch {
      showRefreshError();
    } finally {
      clearTimeout(requestTimeoutId);
      dashboardContent.removeAttribute("aria-busy");
      isRefreshing = false;

      // Subtract request/update time to keep the configured refresh cadence.
      const elapsedMilliseconds = performance.now() - refreshStartedAt;
      const refreshDelayMilliseconds = Math.max(
        0,
        REFRESH_INTERVAL_MS - elapsedMilliseconds,
      );
      refreshTimerId = setTimeout(refreshDashboard, refreshDelayMilliseconds);
    }
  }

  initializeDashboard();

  const heartbeatUrl = dashboardContent.dataset.heartbeatUrl;
  if (heartbeatUrl) {
    navigator.sendBeacon(heartbeatUrl);
    setInterval(() => navigator.sendBeacon(heartbeatUrl), 5000);
  }
})();

