/**
 * Solar Monitor - Frontend JavaScript
 * Features:
 * - Real-time data via SignalR & MQTT Proxy Tunnel
 * - Battery Cell monitoring (16 cells) with Day Max voltage
 * - SOC (State of Charge) Chart & Daily Energy Profiles
 * - Energy flow visualization with blink effect on value change
 * - Chart.js visualizations
 * - Mobile optimized interface
 * - Dynamic Ngrok / Localhost origin compatibility
 */

document.addEventListener('DOMContentLoaded', function () {
    // ========================================
    // INITIALIZATION
    // ========================================

    // Set up today's date as default
    const today = new Date();
    const dateInput = document.getElementById('dateInput');
    if (dateInput) {
        dateInput.value = formatDate(today);
    }

    // Get deviceId from URL parameter
    const urlParams = new URLSearchParams(window.location.search);
    const deviceIdParam = urlParams.get('deviceId');
    if (deviceIdParam) {
        const deviceIdInput = document.getElementById('deviceId');
        if (deviceIdInput) {
            deviceIdInput.value = deviceIdParam;
        }
    }

    // Handle Enter key in deviceId input
    const deviceIdInput = document.getElementById('deviceId');
    if (deviceIdInput) {
        deviceIdInput.addEventListener('keypress', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                fetchData();
            }
        });
    }

    // Configure Chart.js defaults
    configureChartDefaults();

    // Chart objects
    let pvChart, batChart, loadChart, gridChart, essentialChart, socChart;

    // SignalR connection
    let connection;
    let currentDeviceId = '';

    // SOC History for real-time chart - REAL DATA ONLY (no mock)
    let socHistory = [];
    const MAX_SOC_HISTORY = 1440; // 24 hours * 60 (1-min intervals)
    let socDataReceived = false; // Track if we received real SOC data

    // Store previous values for blink detection
    let previousValues = {};
    let previousCellValues = {};
    let lastCellUpdateTime = 0;

    // Battery cell communication state
    let hasCellData = false; // True only after receiving REAL data from MQTT
    let cellDataReceived = false; // Flag to track if we ever received cell data

    // Polling and timers state (declared at top to prevent Temporal Dead Zone errors)
    let directPollTimer = null;
    let isPollingInProgress = false;
    let currentPollController = null;
    let currentPollTimeout = null;
    let cellWaitingTimeout = null;
    let socPollingInterval = null;

    // ========================================
    // EVENT LISTENERS
    // ========================================

    // View button
    const viewBtn = document.getElementById('viewBtn');
    if (viewBtn) {
        viewBtn.addEventListener('click', fetchData);
    }

    // Date navigation
    const prevDayBtn = document.getElementById('prevDay');
    const nextDayBtn = document.getElementById('nextDay');
    if (prevDayBtn) prevDayBtn.addEventListener('click', () => changeDate(-1));
    if (nextDayBtn) nextDayBtn.addEventListener('click', () => changeDate(1));

    // Summary card clicks - scroll to section
    const cardSections = [
        { cardId: 'pv-card', sectionId: 'pv-section' },
        { cardId: 'bat-charge-card', sectionId: 'bat-section' },
        { cardId: 'bat-discharge-card', sectionId: 'bat-section' },
        { cardId: 'load-card', sectionId: 'load-section' },
        { cardId: 'grid-card', sectionId: 'grid-section' },
        { cardId: 'essential-card', sectionId: 'essential-section' }
    ];

    cardSections.forEach(({ cardId, sectionId }) => {
        const card = document.getElementById(cardId);
        if (card) {
            card.addEventListener('click', () => scrollToElement(sectionId));
        }
    });

    // Hero section toggle (mobile)
    const heroToggle = document.getElementById('heroToggle');
    const heroContent = document.getElementById('heroContent');
    if (heroToggle && heroContent) {
        heroToggle.addEventListener('click', () => {
            heroContent.classList.toggle('collapsed');
            heroToggle.classList.toggle('rotated');
        });
    }

    // Battery cell section toggle
    const cellSectionHeader = document.getElementById('cellSectionHeader');
    const cellSectionContent = document.getElementById('cellSectionContent');
    const toggleIcon = document.getElementById('toggleIcon');
    const toggleText = document.getElementById('toggleText');

    if (cellSectionHeader && cellSectionContent) {
        cellSectionHeader.addEventListener('click', (e) => {
            // Ignore if clicking on reload button
            if (e.target.closest('#reloadCellBtn')) return;

            const isCollapsed = cellSectionContent.classList.toggle('hidden');
            if (toggleIcon) {
                toggleIcon.style.transform = isCollapsed ? 'rotate(180deg)' : 'rotate(0deg)';
            }
            if (toggleText) {
                toggleText.textContent = isCollapsed ? 'Hiện' : 'Ẩn';
            }
        });
    }

    // Reload cell data button
    const reloadCellBtn = document.getElementById('reloadCellBtn');
    if (reloadCellBtn) {
        reloadCellBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            requestCellDataReload();
        });
    }

    // Change device button - show hero section again
    const changeDeviceBtn = document.getElementById('changeDeviceBtn');
    if (changeDeviceBtn) {
        changeDeviceBtn.addEventListener('click', () => {
            const heroSection = document.getElementById('heroSection');
            const compactSearch = document.getElementById('compactSearch');

            if (heroSection) {
                heroSection.classList.remove('hidden');
            }
            if (compactSearch) {
                compactSearch.classList.add('hidden');
            }
            // Focus on device ID input
            const deviceIdInput = document.getElementById('deviceId');
            if (deviceIdInput) {
                deviceIdInput.focus();
                deviceIdInput.select();
            }
        });
    }

    // Compact date navigation
    const prevDayCompact = document.getElementById('prevDayCompact');
    const nextDayCompact = document.getElementById('nextDayCompact');
    if (prevDayCompact) prevDayCompact.addEventListener('click', () => changeDate(-1));
    if (nextDayCompact) nextDayCompact.addEventListener('click', () => changeDate(1));

    // Initialize SignalR
    initializeSignalRConnection();

    // Auto-fetch if deviceId in URL
    if (deviceIdParam) {
        fetchData();
    }

    // ========================================
    // CHART CONFIGURATION
    // ========================================

    function configureChartDefaults() {
        Chart.defaults.font.family = "'Inter', 'Segoe UI', 'Helvetica', 'Arial', sans-serif";
        Chart.defaults.color = '#64748b';
        Chart.defaults.elements.line.borderWidth = 2;
        Chart.defaults.elements.point.hitRadius = 8;

        const isDarkMode = document.documentElement.classList.contains('dark') ||
            (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);

        Chart.defaults.scale.grid.color = isDarkMode ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.05)';
        Chart.defaults.scale.ticks.color = isDarkMode ? 'rgba(255, 255, 255, 0.7)' : 'rgba(0, 0, 0, 0.7)';

        // Register custom tooltip positioner to prevent cutoff at chart edges
        Chart.Tooltip.positioners.edgeAware = function(elements, eventPosition) {
            if (!elements.length) return false;

            const chart = this.chart;
            const chartArea = chart.chartArea;
            const tooltipWidth = 140;
            const padding = 20;

            let x = elements[0].element.x;
            let y = elements[0].element.y;

            // Adjust X if tooltip would overflow right edge
            if (x + tooltipWidth/2 > chartArea.right - padding) {
                x = chartArea.right - tooltipWidth - padding;
            }
            // Adjust X if tooltip would overflow left edge
            if (x - tooltipWidth/2 < chartArea.left + padding) {
                x = chartArea.left + tooltipWidth/2 + padding;
            }

            return { x: x, y: y };
        };
    }

    // ========================================
    // SIGNALR CONNECTION
    // ========================================

    function initializeSignalRConnection() {
        console.log("Initializing SignalR connection");

        connection = new signalR.HubConnectionBuilder()
            .withUrl("/deviceHub", {
                headers: { "ngrok-skip-browser-warning": "69420" }
            })
            .withAutomaticReconnect([0, 2000, 10000, 30000])
            .build();

        // Handle real-time data
        connection.on("ReceiveRealTimeData", function (data) {
            console.log("Received real-time data:", data);
            updateRealTimeDisplay(data);
            updateConnectionStatus('connected');
        });

        // Handle battery cell data
        connection.on("ReceiveBatteryCellData", function (data) {
            console.log("Received battery cell data:", data);
            updateBatteryCellDisplay(data);
        });

        // Handle SOC data
        connection.on("ReceiveSOCData", function (data) {
            console.log("Received SOC data:", data);
            updateSOCChart(data);
        });

        connection.on("SubscriptionConfirmed", function (deviceId) {
            console.log(`Subscribed to device: ${deviceId}`);
            updateConnectionStatus('connected');
        });

        startSignalRConnection();
    }

    function updateConnectionStatus(status) {
        const indicator = document.getElementById('connectionIndicator');
        const text = document.getElementById('connectionText');

        if (indicator) {
            indicator.className = 'w-2.5 h-2.5 rounded-full';
            if (status === 'connected') {
                indicator.classList.add('status-connected');
            } else if (status === 'connecting') {
                indicator.classList.add('status-connecting');
            } else {
                indicator.classList.add('status-disconnected');
            }
        }

        if (text) {
            if (status === 'connected') {
                text.textContent = 'Đã kết nối';
            } else if (status === 'connecting') {
                text.textContent = 'Đang kết nối...';
            } else {
                text.textContent = 'Mất kết nối';
            }
        }
    }

    async function startSignalRConnection() {
        updateConnectionStatus('connecting');
        try {
            await connection.start();
            console.log("SignalR Connected");
            updateConnectionStatus('connected');

            let deviceToSubscribe = document.getElementById('deviceId')?.value?.trim();
            if (!deviceToSubscribe) {
                deviceToSubscribe = urlParams.get('deviceId');
            }

            if (deviceToSubscribe) {
                subscribeToDevice(deviceToSubscribe);
            }
        } catch (err) {
            console.error("SignalR Connection Error:", err);
            updateConnectionStatus('disconnected');
            setTimeout(startSignalRConnection, 5000);
        }
    }

    function subscribeToDevice(deviceId) {
        if (!deviceId || deviceId === currentDeviceId || !connection || connection.state !== "Connected") {
            return;
        }

        if (currentDeviceId) {
            connection.invoke("UnsubscribeFromDevice", currentDeviceId)
                .catch(err => console.error("Unsubscribe error:", err));
        }

        connection.invoke("SubscribeToDevice", deviceId)
            .then(() => {
                currentDeviceId = deviceId;
                console.log(`Subscribed to: ${deviceId}`);
            })
            .catch(err => console.error("Subscribe error:", err));
    }

    connection.onclose(async () => {
        console.log("SignalR connection closed");
        updateConnectionStatus('disconnected');
        await startSignalRConnection();
    });

    // ========================================
    // DATA FETCHING
    // ========================================

    function fetchData() {
        const deviceId = document.getElementById('deviceId')?.value?.trim();
        const date = document.getElementById('dateInput')?.value;

        if (!deviceId) {
            showError('Vui lòng nhập Device ID');
            return;
        }

        // Update URL
        const url = new URL(window.location);
        url.searchParams.set('deviceId', deviceId);
        window.history.pushState({}, '', url);

        // Update title
        document.title = `Solar Monitor - ${deviceId}`;

        // Clean up previous polling timers
        stopDirectPolling();
        stopSOCPolling();

        // Subscribe to real-time
        subscribeToDevice(deviceId);

        showLoading(true);
        hideError();

        fetch(`/device/${encodeURIComponent(deviceId)}?date=${encodeURIComponent(date)}`, {
            headers: { "ngrok-skip-browser-warning": "69420" }
        })
            .then(async response => {
                if (!response.ok) {
                    // Try to parse error response as JSON
                    try {
                        const errorData = await response.json();
                        throw new Error(errorData.error || errorData.suggestion || `Server error: ${response.status}`);
                    } catch (parseError) {
                        // If parsing fails, use status code message
                        if (response.status === 404) {
                            throw new Error(`Không tìm thấy thiết bị "${deviceId}". Vui lòng kiểm tra lại Device ID.`);
                        }
                        throw new Error(`Server error: ${response.status}`);
                    }
                }
                return response.json();
            })
            .then(data => {
                const currentActiveId = (currentDeviceId || document.getElementById('deviceId')?.value)?.trim()?.toUpperCase();
                if (currentActiveId && currentActiveId !== deviceId.trim().toUpperCase()) {
                    return; // Ignore stale fetch result if user switched device
                }
                // Check if data contains error field (server returned error as 200)
                if (data.error) {
                    throw new Error(data.error);
                }
                console.log("Data received:", data);
                processData(data);
                showCompactSearchBar(deviceId, date);
                updateConnectionStatus('connected');
                startDirectPolling(deviceId);

                // Fetch SOC timeline data from Lumentree API
                fetchSOCData(deviceId, date);
            })
            .catch(error => {
                console.error("Fetch error:", error);
                stopDirectPolling();
                showError(error.message || 'Lỗi tải dữ liệu. Vui lòng thử lại sau.');
            })
            .finally(() => {
                showLoading(false);
            });
    }

    // Auto-polling qua REST API trực tiếp mỗi 20s (không qua proxy, độ trễ <100ms)
    function stopDirectPolling() {
        if (directPollTimer) {
            clearInterval(directPollTimer);
            directPollTimer = null;
        }
        if (currentPollTimeout) {
            clearTimeout(currentPollTimeout);
            currentPollTimeout = null;
        }
        if (currentPollController) {
            currentPollController.abort();
            currentPollController = null;
        }
        isPollingInProgress = false;
    }
    window.addEventListener('beforeunload', () => {
        stopDirectPolling();
        if (cellWaitingTimeout) {
            clearTimeout(cellWaitingTimeout);
            cellWaitingTimeout = null;
        }
    });
    function startDirectPolling(deviceId) {
        stopDirectPolling();
        const targetDeviceId = deviceId ? deviceId.trim().toUpperCase() : '';
        if (!targetDeviceId) return;

        directPollTimer = setInterval(async () => {
            const currentActiveId = (currentDeviceId || document.getElementById('deviceId')?.value)?.trim()?.toUpperCase();
            if (currentActiveId !== targetDeviceId) {
                stopDirectPolling();
                return;
            }
            const currentDateInput = document.getElementById('dateInput')?.value;
            const todayStr = formatDate(new Date());
            if (currentDateInput === todayStr && !document.hidden && !isPollingInProgress) {
                isPollingInProgress = true;
                const controller = new AbortController();
                currentPollController = controller;
                currentPollTimeout = setTimeout(() => controller.abort(), 15000);
                try {
                    const res = await fetch(`/device/${encodeURIComponent(targetDeviceId)}?date=${encodeURIComponent(todayStr)}`, {
                        headers: { "ngrok-skip-browser-warning": "69420" },
                        signal: controller.signal
                    });
                    if (res.ok) {
                        const data = await res.json();
                        const activeIdNow = (currentDeviceId || document.getElementById('deviceId')?.value)?.trim()?.toUpperCase();
                        const activeDate = document.getElementById('dateInput')?.value;
                        if (data && !data.error && activeIdNow === targetDeviceId && activeDate === todayStr) {
                            processData(data, true);
                            updateConnectionStatus('connected');
                        }
                    }
                } catch (err) {
                    console.debug("Direct poll error:", err);
                } finally {
                    if (currentPollTimeout) {
                        clearTimeout(currentPollTimeout);
                        currentPollTimeout = null;
                    }
                    if (currentPollController === controller) {
                        currentPollController = null;
                    }
                    isPollingInProgress = false;
                }
            }
        }, 20000);
    }

    let currentSocQueryDate = '';

    // Fetch SOC timeline data from lumentree.net API
    function fetchSOCData(deviceId, date, isPolling = false) {
        const queryDate = date || document.getElementById('dateInput')?.value || formatDate(new Date());

        fetch(`/device/${encodeURIComponent(deviceId)}/soc?date=${encodeURIComponent(queryDate)}`, {
            headers: { "ngrok-skip-browser-warning": "69420" }
        })
            .then(response => {
                if (!response.ok) throw new Error(`SOC API error: ${response.status}`);
                return response.json();
            })
            .then(data => {
                const activeDeviceId = (currentDeviceId || document.getElementById('deviceId')?.value)?.trim()?.toUpperCase();
                const activeDate = document.getElementById('dateInput')?.value || formatDate(new Date());
                if (activeDeviceId && activeDeviceId !== (deviceId?.trim()?.toUpperCase() || '')) {
                    return;
                }
                if (activeDate && activeDate !== queryDate) {
                    return;
                }

                // Scoped by date and session: reset history on date change or initial load to prevent interleaving
                if (!isPolling || currentSocQueryDate !== queryDate) {
                    socHistory = [];
                    currentSocQueryDate = queryDate;
                }

                console.log("SOC timeline data received:", data);

                // Get timeline array from response
                const rawTimeline = data?.timeline || data?.history || [];
                const timeline = Array.isArray(rawTimeline)
                    ? rawTimeline.filter(Boolean).map(item => ({
                        ...item,
                        t: item?.t || item?.time || ''
                    }))
                    : [];
                if (timeline.length > 0) {
                    // Load all SOC data points from timeline
                    loadSOCTimeline(timeline);
                    console.log(`Loaded ${timeline.length} SOC data points from Lumentree API`);
                } else {
                    console.warn("No SOC timeline data available");
                    if (!isPolling || socHistory.length === 0) {
                        updateSOCStats(null, null, null, 0);
                        showSOCUnavailableNotice();
                    }
                }
            })
            .catch(error => {
                const activeDeviceId = (currentDeviceId || document.getElementById('deviceId')?.value)?.trim()?.toUpperCase();
                const activeDate = document.getElementById('dateInput')?.value || formatDate(new Date());
                if (activeDeviceId && activeDeviceId !== (deviceId?.trim()?.toUpperCase() || '')) {
                    return;
                }
                if (activeDate && activeDate !== queryDate) {
                    return;
                }
                console.error("SOC data fetch error:", error);
                if (!isPolling || socHistory.length === 0) {
                    showSOCUnavailableNotice();
                }
            });
    }

    function showSOCUnavailableNotice() {
        if (socChart) {
            socChart.destroy();
            socChart = null;
        }
        let waitingDiv = document.getElementById('soc-waiting');
        if (!waitingDiv) {
            const ctx = document.getElementById('socChart');
            const container = ctx?.parentElement;
            if (container) {
                waitingDiv = document.createElement('div');
                waitingDiv.id = 'soc-waiting';
                waitingDiv.className = 'absolute inset-0 flex flex-col items-center justify-center bg-gradient-to-br from-emerald-50/90 to-green-50/90 dark:from-emerald-900/40 dark:to-green-900/40 rounded-lg';
                container.style.position = 'relative';
                container.appendChild(waitingDiv);
            }
        }
        if (waitingDiv) {
            waitingDiv.innerHTML = `
                <div class="flex items-center gap-2 mb-2 text-slate-500 dark:text-slate-400">
                    <i data-lucide="info" class="w-5 h-5 text-cyan-500"></i>
                    <span class="text-sm font-semibold">Dữ liệu SOC theo giờ tạm thời ngoại tuyến</span>
                </div>
                <p class="text-xs text-slate-400 dark:text-slate-500 text-center px-4">
                    Máy chủ LEHT đang bảo trì lịch sử SOC theo giờ. Dữ liệu nạp/xả pin và công suất vẫn hiển thị đầy đủ ở các biểu đồ bên dưới.
                </p>
            `;
            if (window.lucide) lucide.createIcons();
        }
    }

    // Load SOC timeline data into chart
    function loadSOCTimeline(timeline) {
        // Merge data points from timeline without dropping accumulated real-time points
        timeline.forEach(item => {
            let timeLabel = String(item?.t || item?.time || '');
            const socVal = Number(item?.soc);
            if (item && item.soc !== undefined && item.soc !== null && !Number.isNaN(socVal) && timeLabel) {
                // Normalize HH:mm format if needed (pad single-digit hour, strip seconds, ignore leading date)
                const match = timeLabel.match(/(?:^|\s|T)(\d{1,2}):(\d{2})/);
                if (match) {
                    timeLabel = `${match[1].padStart(2, '0')}:${match[2]}`;
                } else if (!/^\d{2}:\d{2}$/.test(timeLabel)) {
                    return;
                }
                const existingIdx = socHistory.findIndex(p => p.time === timeLabel);
                if (existingIdx >= 0) {
                    socHistory[existingIdx].soc = socVal;
                    socHistory[existingIdx].timestamp = Date.now();
                } else {
                    socHistory.push({
                        time: timeLabel,
                        soc: socVal,
                        timestamp: Date.now()
                    });
                }
            }
        });

        socHistory.sort((a, b) => a.time.localeCompare(b.time));

        if (socHistory.length > 0) {
            socDataReceived = true;
            updateSOCChartRealTime();
            console.log(`SOC chart updated with ${socHistory.length} points`);
        }
    }

    function showCompactSearchBar(deviceId, date) {
        // Hide hero section and show compact bar
        const heroSection = document.getElementById('heroSection');
        const compactSearch = document.getElementById('compactSearch');
        const deviceIdDisplay = document.getElementById('deviceIdDisplay');
        const dateDisplay = document.getElementById('dateDisplay');
        const fixedCalculateBtn = document.getElementById('fixedCalculateBtn');

        if (heroSection) {
            heroSection.classList.add('hidden');
        }
        if (compactSearch) {
            compactSearch.classList.remove('hidden');
        }
        if (deviceIdDisplay) {
            deviceIdDisplay.textContent = deviceId;
        }
        if (dateDisplay) {
            const dateObj = new Date(date);
            dateDisplay.textContent = dateObj.toLocaleDateString('vi-VN');
        }
        // Show fixed calculate button on desktop only (avoid overlapping FAB on mobile)
        if (fixedCalculateBtn && window.innerWidth >= 640) {
            fixedCalculateBtn.classList.remove('hidden');
        }
    }

    // ========================================
    // DATA PROCESSING
    // ========================================

    function processData(data, isPolling = false) {
        // Show all sections including batteryCellSection
        showElement('deviceInfo');
        showElement('summaryStats');
        showElement('chart-section');
        showElement('realTimeFlow');
        showElement('pvInputsSection');
        showElement('batteryCellSection'); // Always show, will display waiting message
        showElement('socChartSection');

        if (!isPolling) {
            // Reset cell data state for new device
            hasCellData = false;
            cellDataReceived = false;
        }

        // Update device info
        updateDeviceInfo(data.deviceInfo);

        // Update summary stats (convert from 0.1kWh to kWh)
        const pvKwh = ((data.pv?.tableValue ?? 0) / 10);
        updateValue('pv-total', pvKwh.toFixed(1) + ' kWh');
        updateValue('bat-charge', (((data.bat?.bats?.[0]?.tableValue) ?? 0) / 10).toFixed(1) + ' kWh');
        updateValue('bat-discharge', (((data.bat?.bats?.[1]?.tableValue) ?? 0) / 10).toFixed(1) + ' kWh');

        let loadKwh = ((data.load?.tableValue ?? 0) / 10);
        let essKwh = ((data.essentialLoad?.tableValue ?? 0) / 10);
        if (loadKwh === 0 && essKwh > 0) {
            // Hệ hòa lưới bám tải Lumentree ghi nhận tổng tải tiêu thụ gia đình ở essentialLoad
            loadKwh = essKwh;
            if (!data.load) data.load = {};
            data.load.tableValue = data.essentialLoad?.tableValue ?? 0;
            if (!data.load?.tableValueInfo?.length && data.essentialLoad?.tableValueInfo?.length) {
                data.load.tableValueInfo = [...data.essentialLoad.tableValueInfo];
            }
        }
        updateValue('load-total', loadKwh.toFixed(1) + ' kWh');
        updateValue('grid-total', ((data.grid?.tableValue ?? 0) / 10).toFixed(1) + ' kWh');
        updateValue('essential-total', essKwh.toFixed(1) + ' kWh');

        // Cập nhật phụ đề header PV: X.X kWh/Ngày chuẩn App Android
        const pvHeaderEl = document.getElementById('pvDailyHeader');
        if (pvHeaderEl) {
            pvHeaderEl.textContent = `PV: ${pvKwh.toFixed(1)}kWh/Ngày`;
        }

        // Cập nhật ngay các thẻ Luồng Năng Lượng Thời Gian Thực nếu API trả về số liệu tức thời
        if (data.realtimeData && data.realtimeData.data) {
            const rd = data.realtimeData.data;
            updateRealTimeDisplay({
                deviceId: data.deviceInfo?.deviceId || '',
                pvTotalPower: rd.totalPvPower || 0,
                pv1Power: rd.pv1Power || 0,
                pv1Voltage: rd.pv1Voltage || 0,
                pv1Current: rd.pv1Current,
                pv2Power: rd.pv2Power || 0,
                pv2Voltage: rd.pv2Voltage || 0,
                pv2Current: rd.pv2Current,
                gridValue: rd.gridPowerFlow || 0,
                gridVoltageValue: rd.acInputVoltage ?? 220,
                batteryPercent: rd.batterySoc || 0,
                batteryValue: rd.batteryPower || 0,
                batteryStatus: rd.batteryStatus || "Idle",
                deviceTempValue: rd.temperature || 0,
                essentialValue: rd.essentialPower !== undefined ? rd.essentialPower : 0,
                loadValue: (rd.homeLoad !== undefined && rd.homeLoad > 0) ? rd.homeLoad : (rd.acOutputPower || 0),
                timestamp: data.timestamp || new Date().toISOString()
            });
        }

        if (!isPolling) {
            // Update charts on initial/manual load
            updateCharts(data);

            // Initialize battery cells with waiting message (no mock data)
            initializeBatteryCellsWaiting();

            // Initialize SOC chart with waiting message
            // SOC data will be loaded from fetchSOCData() called after this
            initializeSOCChartWaiting();

            // Start SOC polling (every 5 minutes to get new data points)
            const deviceId = document.getElementById('deviceId')?.value?.trim();
            if (deviceId) {
                startSOCPolling(deviceId);
            }
        }
    }

    function updateDeviceInfo(deviceInfo) {
        let deviceText = deviceInfo.deviceId;
        if (deviceInfo.remarkName && deviceInfo.remarkName.length > 0) {
            deviceText += " - " + deviceInfo.remarkName;
        }

        updateValue('device-id', deviceText.substring(0, 40));
        updateValue('device-type', deviceInfo.deviceType);
        updateValue('inverter-type', deviceInfo.deviceType);
        updateValue('device-status', deviceInfo.onlineStatus === 1 ? 'Online' : 'Offline');

        // Update status color
        const statusEl = document.getElementById('device-status');
        if (statusEl) {
            if (deviceInfo.onlineStatus === 1) {
                statusEl.className = 'text-green-600 dark:text-green-400 font-semibold';
            } else {
                statusEl.className = 'text-red-600 dark:text-red-400 font-semibold';
            }
        }
    }

    // ========================================
    // REAL-TIME DISPLAY UPDATE
    // ========================================

    function updateRealTimeDisplay(data) {
        // PV - with blink effect (chuẩn App Android: ban đêm không có nắng hiển thị --W và --V)
        const pvPower = (data.pvTotalPower !== undefined && data.pvTotalPower !== null)
            ? data.pvTotalPower
            : ((data.pv1Power || 0) + (data.pv2Power || 0));
        if (pvPower === 0) {
            updateValue('pv-power', '--W');
            let pvDesc = '--V';
            const v1 = data.pv1Voltage || 0;
            const v2 = data.pv2Voltage || 0;
            if (v2 > 0) {
                pvDesc = `${v1}V | ${v2}V`;
            } else if (v1 > 0) {
                pvDesc = `${v1}V`;
            }
            updateValue('pv-desc', pvDesc);
        } else if (data.pv2Power || data.pv2Voltage > 0) {
            const p1 = Number(data.pv1Power) || 0;
            const v1 = Number(data.pv1Voltage) || 0;
            const p2 = Number(data.pv2Power) || 0;
            const v2 = Number(data.pv2Voltage) || 0;
            updateValue('pv-power', `${pvPower}W`);
            updateValueHTML('pv-desc', `
                <span class="hidden sm:inline text-amber-500">S1:</span>
                <span class="text-amber-400 font-bold text-[11px] sm:text-sm">${p1}W</span>
                <span class="text-[9px] sm:text-xs text-gray-400">${v1}V</span>
                <span class="text-gray-500 mx-0.5">|</span>
                <span class="hidden sm:inline text-amber-500">S2:</span>
                <span class="text-amber-400 font-bold text-[11px] sm:text-sm">${p2}W</span>
                <span class="text-[9px] sm:text-xs text-gray-400">${v2}V</span>
            `);
        } else {
            updateValue('pv-power', `${pvPower}W`);
            const pvDesc = data.pv1Voltage > 0 ? `${data.pv1Voltage}V` : '--V';
            updateValue('pv-desc', pvDesc);
        }

        // Cập nhật 2 đầu vào quang điện (MPPT 1 & MPPT 2) chuẩn App Android Lumentree (--V, --W, --A khi không có nắng)
        const pv1V = data.pv1Voltage || 0;
        const pv1P = data.pv1Power || 0;
        let pv1I = '0.0';
        if (typeof data.pv1Current === 'number' && data.pv1Current > 0) {
            pv1I = data.pv1Current.toFixed(1);
        } else if (pv1V > 0 && pv1P > 0) {
            pv1I = (pv1P / pv1V).toFixed(1);
        }

        const pv2V = data.pv2Voltage || 0;
        const pv2P = data.pv2Power || 0;
        let pv2I = '0.0';
        if (typeof data.pv2Current === 'number' && data.pv2Current > 0) {
            pv2I = data.pv2Current.toFixed(1);
        } else if (pv2V > 0 && pv2P > 0) {
            pv2I = (pv2P / pv2V).toFixed(1);
        }

        const totalPvLive = pvPower || (pv1P + pv2P);
        const formatMpptPower = (power, totalLive) => {
            if (power > 0) return `${power} W`;
            if (totalLive === 0) return '--W';
            return '0 W';
        };
        const formatMpptCurrent = (currentStr) => (parseFloat(currentStr) > 0 ? `${currentStr} A` : '--A');

        updateValue('pv-total-live', totalPvLive > 0 ? `${totalPvLive} W` : `--W`);
        updateValue('pv1-voltage', pv1V > 0 ? `${pv1V} V` : `--V`);
        updateValue('pv1-power', formatMpptPower(pv1P, totalPvLive));
        updateValue('pv1-current', formatMpptCurrent(pv1I));

        updateValue('pv2-voltage', pv2V > 0 ? `${pv2V} V` : `--V`);
        updateValue('pv2-power', formatMpptPower(pv2P, totalPvLive));
        updateValue('pv2-current', formatMpptCurrent(pv2I));

        // Grid - with blink effect
        updateValue('grid-power', `${data.gridValue}W`);
        updateValue('grid-voltage', `${data.gridVoltageValue}V`);

        // Battery
        const batteryPercent = data.batteryPercent || 0;

        // Update battery percent display in icon - with blink
        updateValue('battery-percent-icon', `${batteryPercent}%`);

        // Update battery fill level - horizontal bar like phone battery
        const batteryFill = document.getElementById('battery-fill');
        if (batteryFill) {
            batteryFill.style.width = `${batteryPercent}%`;
            // Change color based on level: Red 0-20%, Yellow 21-50%, Green 51-100%
            if (batteryPercent <= 20) {
                batteryFill.className = 'absolute left-0 top-0 bottom-0 bg-red-500 transition-all duration-500';
            } else if (batteryPercent <= 50) {
                batteryFill.className = 'absolute left-0 top-0 bottom-0 bg-yellow-500 transition-all duration-500';
            } else {
                batteryFill.className = 'absolute left-0 top-0 bottom-0 bg-green-500 transition-all duration-500';
            }
        }

        // Update battery status text - with blink
        if (data.batteryStatus === "Discharging") {
            updateValueHTML('battery-status-text', `<span class="text-red-500">Đang xả</span>`);
        } else if (data.batteryStatus === "Charging") {
            updateValueHTML('battery-status-text', `<span class="text-green-500">Đang sạc</span>`);
        } else {
            updateValueHTML('battery-status-text', `<span class="text-slate-500">Chờ</span>`);
        }

        // Battery power - with blink (chuẩn App Android: hiển thị số dương công suất kèm trạng thái)
        const batWatts = Math.abs(Number(data.batteryValue) || 0);
        if (data.batteryStatus === "Discharging") {
            updateValueHTML('battery-power', `<span class="text-amber-500 dark:text-amber-400 font-bold">${batWatts}W</span>`);
        } else if (data.batteryStatus === "Charging") {
            updateValueHTML('battery-power', `<span class="text-green-600 dark:text-green-400 font-bold">+${batWatts}W</span>`);
        } else {
            updateValueHTML('battery-power', `<span class="text-slate-600 dark:text-slate-300 font-bold">${batWatts}W</span>`);
        }

        if (batteryPercent > 0) {
            const currentEl = document.getElementById('soc-current');
            if (currentEl && currentEl.textContent === '--%') {
                updateSOCStats(batteryPercent, batteryPercent, batteryPercent, socHistory.length);
            }
        }

        // Battery Voltage (Điện Áp Pin giao tiếp) - display in Cell section
        if (data.batteryVoltage) {
            updateValue('batteryVoltageDisplay', `${data.batteryVoltage.toFixed(1)}V`);
        }

        // Other values - with blink effect (chuẩn App Android: hiển thị cả °C và °F)
        const parsedTemp = parseFloat(data.deviceTempValue);
        const tempC = Number.isFinite(parsedTemp) ? parsedTemp : null;
        const tempStr = tempC !== null ? `${tempC}°C(${Math.round(tempC * 9 / 5 + 32)}°F)` : '--';
        updateValue('device-temp', tempStr);
        updateValue('device-temp-info', tempStr);
        updateValue('essential-power', `${data.essentialValue}W`);
        updateValue('load-power', `${data.loadValue}W`);

        // Update AC Out power (from inverterAcOutPower)
        if (data.inverterAcOutPower !== undefined) {
            updateValue('acout-power', `${data.inverterAcOutPower}W`);
        }

        // Update flow statuses
        updateFlowStatus('pv-flow', pvPower > 0);
        updateFlowStatus('grid-flow', data.gridValue > 0);
        updateFlowStatus('battery-flow', data.batteryValue !== 0);
        updateFlowStatus('essential-flow', data.essentialValue > 0);
        updateFlowStatus('load-flow', data.loadValue > 0);

        // Update last refresh time with blink
        const now = new Date();
        const timeStr = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}:${String(now.getSeconds()).padStart(2, '0')}`;
        updateValue('lastUpdateTime', `Cập nhật: ${timeStr}`);

        // Update SOC history for real-time chart - PER MINUTE UPDATES (only when viewing today)
        const isViewingToday = (document.getElementById('dateInput')?.value || formatDate(new Date())) === formatDate(new Date());
        if (batteryPercent > 0 && isViewingToday) {
            const now = new Date();
            const timeStr = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;

            // Check if we should add this data point (avoid duplicates within same minute)
            const lastEntry = socHistory.length > 0 ? socHistory[socHistory.length - 1] : null;
            const shouldAddPoint = !lastEntry || lastEntry.time !== timeStr;

            if (shouldAddPoint) {
                // Mark that we received real SOC data
                socDataReceived = true;

                // Add new data point
                socHistory.push({
                    time: timeStr,
                    soc: batteryPercent,
                    timestamp: now.getTime()
                });

                // Keep only last MAX_SOC_HISTORY points (24 hours of per-minute data)
                if (socHistory.length > MAX_SOC_HISTORY) {
                    socHistory = socHistory.slice(-MAX_SOC_HISTORY);
                }

                console.log(`SOC updated: ${batteryPercent}% at ${timeStr} (${socHistory.length} points)`);

                // Update SOC chart with real-time data
                updateSOCChartRealTime();
            } else {
                // Update the current minute's value if it changed
                if (lastEntry && lastEntry.soc !== batteryPercent) {
                    lastEntry.soc = batteryPercent;
                    updateSOCChartRealTime();
                }
            }
        }
    }

    // ========================================
    // BATTERY CELL DISPLAY
    // ========================================

    // Initialize battery cells with waiting message (always visible, no mock data)
    function initializeBatteryCellsWaiting() {
        if (cellWaitingTimeout) {
            clearTimeout(cellWaitingTimeout);
            cellWaitingTimeout = null;
        }
        cellDataReceived = false;

        // Reset values to waiting state
        const cellDayMax = document.getElementById('cellDayMax');
        const cellAvg = document.getElementById('cellAvg');
        const cellMax = document.getElementById('cellMax');
        const cellMin = document.getElementById('cellMin');
        const cellDiffValue = document.getElementById('cellDiffValue');
        const cellCountBadge = document.getElementById('cellCountBadge');
        const cellUpdateTime = document.getElementById('cellUpdateTime');

        if (cellDayMax) cellDayMax.textContent = '--';
        if (cellAvg) cellAvg.textContent = '--';
        if (cellMax) cellMax.textContent = '--';
        if (cellMin) cellMin.textContent = '--';
        if (cellDiffValue) {
            cellDiffValue.textContent = '--';
            cellDiffValue.className = 'text-sm sm:text-lg font-black text-slate-500';
        }
        if (cellCountBadge) cellCountBadge.textContent = '-- cell';
        if (cellUpdateTime) cellUpdateTime.textContent = '--:--:--';

        // Reset day max tracker
        previousValues['cellDayMax_value'] = '0';

        // Show waiting message in cell grid
        const cellGrid = document.getElementById('cellGrid');
        if (cellGrid) {
            cellGrid.innerHTML = `
                <div class="cell-placeholder bg-gradient-to-br from-slate-100 to-slate-200 dark:from-slate-800 dark:to-slate-700 rounded-xl p-6 flex flex-col items-center justify-center gap-3 border-2 border-dashed border-slate-300 dark:border-slate-600">
                    <div class="animate-pulse flex items-center gap-2">
                        <svg class="w-5 h-5 text-teal-500 animate-spin" fill="none" viewBox="0 0 24 24">
                            <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"></circle>
                            <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                        </svg>
                        <span class="text-slate-500 dark:text-slate-400 text-sm font-medium">Đang chờ dữ liệu cell volt...</span>
                    </div>
                    <p class="text-xs text-slate-400 dark:text-slate-500 text-center">Dữ liệu sẽ hiển thị khi nhận được từ thiết bị qua MQTT</p>
                </div>
            `;

            // Sau 6s nếu chưa có dữ liệu MQTT thì hiển thị thông báo trạng thái
            cellWaitingTimeout = setTimeout(() => {
                cellWaitingTimeout = null;
                if (!cellDataReceived && cellGrid && cellGrid.querySelector('.animate-spin')) {
                    cellGrid.innerHTML = `
                        <div class="cell-placeholder bg-gradient-to-br from-slate-50 to-slate-100 dark:from-slate-800 dark:to-slate-700 rounded-xl p-5 flex flex-col items-center justify-center gap-2 border border-slate-200 dark:border-slate-600">
                            <div class="flex items-center gap-2 text-slate-600 dark:text-slate-300">
                                <span class="text-sm font-semibold">⚡ Chi tiết 16 cell pin</span>
                            </div>
                            <p class="text-xs text-slate-400 dark:text-slate-500 text-center max-w-md">
                                Dữ liệu chi tiết từng cell pin yêu cầu luồng MQTT trực tiếp từ inverter (hệ thống đang duy trì kết nối nền qua proxy tunnel). Nhấn "Tải lại" để làm mới.
                            </p>
                        </div>
                    `;
                }
            }, 6000);
        }

        console.log("Battery cell section initialized - waiting for real MQTT data");
    }

    // Request cell data reload via SignalR
    function requestCellDataReload() {
        const reloadBtn = document.getElementById('reloadCellBtn');
        if (reloadBtn) {
            // Add spinning animation
            reloadBtn.classList.add('animate-spin');
            setTimeout(() => reloadBtn.classList.remove('animate-spin'), 1000);
        }

        // Request new cell data from server
        if (connection && connection.state === "Connected" && currentDeviceId) {
            connection.invoke("RequestBatteryCellData", currentDeviceId)
                .then(() => console.log("Requested cell data reload"))
                .catch(err => console.error("Cell reload error:", err));
        }

        console.log("Cell data reload requested");
    }

    // Update cell update time display
    function updateCellUpdateTime() {
        const now = new Date();
        const timeStr = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}:${String(now.getSeconds()).padStart(2, '0')}`;
        const cellUpdateTimeEl = document.getElementById('cellUpdateTime');
        if (cellUpdateTimeEl) {
            cellUpdateTimeEl.textContent = timeStr;
        }
    }

    function updateBatteryCellDisplay(data) {
        if (!data || !data.cells) return;

        if (cellWaitingTimeout) {
            clearTimeout(cellWaitingTimeout);
            cellWaitingTimeout = null;
        }

        const cells = data.cells;
        // Lithium cells must be within 2.0V - 4.5V range
        const validCells = cells.filter(v => v >= 2.0 && v <= 4.5);

        // A valid battery pack has at least 4 valid lithium cells
        if (validCells.length < 4) {
            console.log("No valid cell data - device may not support cell monitoring");
            showNoCellCommunication();
            return;
        }

        // Mark that we have received real cell data
        cellDataReceived = true;
        hasCellData = true;

        console.log("Received real cell data from MQTT:", validCells.length, "cells");

        // Update cell update time
        updateCellUpdateTime();

        // Calculate statistics
        const avg = validCells.reduce((a, b) => a + b, 0) / validCells.length;
        const max = Math.max(...validCells);
        const min = Math.min(...validCells);
        const diff = max - min;

        // Update cell count badge
        const cellCountBadge = document.getElementById('cellCountBadge');
        if (cellCountBadge) {
            cellCountBadge.textContent = `${validCells.length} cell`;
        }

        // Update summary with blink effect
        updateValue('cellAvg', avg.toFixed(3) + 'V');
        updateValue('cellMax', max.toFixed(3) + 'V');
        updateValue('cellMin', min.toFixed(3) + 'V');
        updateValue('cellDiffValue', diff.toFixed(3) + 'V');

        // Update day max voltage from API data (if available)
        if (data.maximumVoltage) {
            updateValue('cellDayMax', data.maximumVoltage.toFixed(3) + 'V');
        } else {
            // Track max voltage during the session
            const currentDayMax = parseFloat(previousValues['cellDayMax_value'] || '0');
            if (max > currentDayMax) {
                previousValues['cellDayMax_value'] = max.toString();
                updateValue('cellDayMax', max.toFixed(3) + 'V');
            }
        }

        // Update diff color
        const diffEl = document.getElementById('cellDiffValue');
        if (diffEl) {
            diffEl.className = 'text-sm sm:text-lg font-black';
            if (diff > 0.05) {
                diffEl.classList.add('text-red-600', 'dark:text-red-400');
            } else if (diff > 0.02) {
                diffEl.classList.add('text-amber-600', 'dark:text-amber-400');
            } else {
                diffEl.classList.add('text-green-600', 'dark:text-green-400');
            }
        }

        // Track update time for communication status
        const currentTime = Date.now();
        lastCellUpdateTime = currentTime;

        // Generate cell grid dynamically with blink effect and communication status
        const cellGrid = document.getElementById('cellGrid');
        if (cellGrid) {
            let gridHtml = '<div class="grid">';

            cells.forEach((voltage, index) => {
                const cellKey = `cell_${index}`;
                const prevVoltage = previousCellValues[cellKey];
                const hasChanged = prevVoltage !== undefined && prevVoltage !== voltage;
                previousCellValues[cellKey] = voltage;

                // Check communication status (voltage = 0 means no communication)
                const noCommunication = voltage === 0 || voltage === null || voltage === undefined;

                if (noCommunication) {
                    // Cell has no communication
                    gridHtml += `
                        <div class="cell-item cell-no-communication relative">
                            <span class="cell-label">C${index + 1}</span>
                            <span class="cell-voltage">N/A</span>
                            <span class="text-[8px] text-red-400 block">Mất kết nối</span>
                        </div>
                    `;
                } else {
                    const deviation = Math.abs(voltage - avg);
                    let colorClass = 'cell-default';

                    if (deviation < 0.02) {
                        colorClass = 'cell-good';
                    } else if (deviation < 0.05) {
                        colorClass = 'cell-ok';
                    } else {
                        colorClass = 'cell-warning';
                    }

                    // Add blink class if value changed
                    const blinkClass = hasChanged ? 'cell-blink' : '';

                    gridHtml += `
                        <div class="cell-item ${colorClass} ${blinkClass}">
                            <span class="cell-label">C${index + 1}</span>
                            <span class="cell-voltage">${voltage.toFixed(3)}V</span>
                        </div>
                    `;
                }
            });

            gridHtml += '</div>';

            // Add communication status indicator
            const commStatus = validCells.length === cells.length ?
                '<span class="text-green-500">✓ Tất cả cell đang giao tiếp</span>' :
                `<span class="text-amber-500">⚠ ${cells.length - validCells.length} cell mất kết nối</span>`;

            gridHtml += `<div class="text-center mt-2 text-xs">${commStatus}</div>`;

            cellGrid.innerHTML = gridHtml;
        }
    }

    // Show message when device doesn't support cell monitoring
    function showNoCellCommunication() {
        const cellGrid = document.getElementById('cellGrid');
        if (cellGrid) {
            cellGrid.innerHTML = `
                <div class="cell-placeholder bg-gradient-to-br from-amber-50 to-orange-50 dark:from-amber-900/20 dark:to-orange-900/20 rounded-xl p-6 flex flex-col items-center justify-center gap-3 border-2 border-dashed border-amber-300 dark:border-amber-700">
                    <div class="flex items-center gap-2">
                        <svg class="w-5 h-5 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"></path>
                        </svg>
                        <span class="text-amber-600 dark:text-amber-400 text-sm font-medium">Thiết bị không hỗ trợ giám sát cell</span>
                    </div>
                    <p class="text-xs text-amber-500 dark:text-amber-500 text-center">Pin của thiết bị này không có tính năng giao tiếp cell voltage</p>
                </div>
            `;
        }

        // Reset stats
        const cellCountBadge = document.getElementById('cellCountBadge');
        if (cellCountBadge) cellCountBadge.textContent = 'N/A';
    }

    // ========================================
    // SOC CHART - DATA FROM LUMENTREE API
    // ========================================

    // Start polling SOC data every 5 minutes (to get new data points)
    function startSOCPolling(deviceId) {
        // Clear any existing interval
        if (socPollingInterval) {
            clearInterval(socPollingInterval);
        }

        // Poll every 5 minutes (300 seconds) to get new SOC data
        socPollingInterval = setInterval(() => {
            const date = document.getElementById('dateInput')?.value;
            fetchSOCData(deviceId, date, true);
        }, 300000); // 5 minutes

        console.log("SOC polling started - every 5 minutes");
    }

    // Stop SOC polling
    function stopSOCPolling() {
        if (socPollingInterval) {
            clearInterval(socPollingInterval);
            socPollingInterval = null;
            console.log("SOC polling stopped");
        }
    }

    // Initialize SOC chart with waiting message - NO MOCK DATA
    function initializeSOCChartWaiting() {
        const ctx = document.getElementById('socChart');
        if (!ctx) return;

        // Reset SOC data for new device
        socHistory = [];
        socDataReceived = false;

        // Stop any existing polling
        stopSOCPolling();

        // Destroy existing chart if any
        if (socChart) {
            socChart.destroy();
            socChart = null;
        }

        // Show waiting message in chart container
        const container = ctx.parentElement;
        if (container) {
            // Create waiting overlay
            let waitingDiv = document.getElementById('soc-waiting');
            if (!waitingDiv) {
                waitingDiv = document.createElement('div');
                waitingDiv.id = 'soc-waiting';
                waitingDiv.className = 'absolute inset-0 flex flex-col items-center justify-center bg-gradient-to-br from-emerald-50/90 to-green-50/90 dark:from-emerald-900/40 dark:to-green-900/40 rounded-lg';
                waitingDiv.innerHTML = `
                    <div class="animate-pulse flex items-center gap-2 mb-2">
                        <svg class="w-5 h-5 text-emerald-500 animate-spin" fill="none" viewBox="0 0 24 24">
                            <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4"></circle>
                            <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                        </svg>
                        <span class="text-emerald-600 dark:text-emerald-400 text-sm font-medium">Đang chờ dữ liệu SOC...</span>
                    </div>
                    <p class="text-xs text-slate-500 dark:text-slate-400 text-center">Biểu đồ sẽ hiển thị khi nhận được dữ liệu real-time từ MQTT</p>
                `;
                container.style.position = 'relative';
                container.appendChild(waitingDiv);
            }
        }

        // Reset stats display
        updateSOCStats(0, 0, 0, 0);

        console.log("SOC chart initialized - waiting for real MQTT data (no mock data)");
    }

    // Hide waiting message and show chart
    function hideSOCWaitingMessage() {
        const waitingDiv = document.getElementById('soc-waiting');
        if (waitingDiv) {
            waitingDiv.remove();
        }
    }

    // Update SOC chart with real-time data from SignalR - PER MINUTE
    function updateSOCChartRealTime() {
        const ctx = document.getElementById('socChart');
        if (!ctx) return;

        if (socHistory.length === 0) return;

        // Hide waiting message when we have data
        hideSOCWaitingMessage();

        const labels = socHistory.map(item => item.time);
        const values = socHistory.map(item => item.soc);

        // Calculate current SOC stats
        const currentSOC = values[values.length - 1];
        const maxSOC = Math.max(...values);
        const minSOC = Math.min(...values);

        if (socChart) {
            // Update existing chart data
            socChart.data.labels = labels;
            socChart.data.datasets[0].data = values;
            socChart.update('none'); // 'none' for no animation on update
        } else {
            // Create new chart with real data
            socChart = new Chart(ctx, {
                type: 'line',
                data: {
                    labels: labels,
                    datasets: [{
                        label: 'SOC (%)',
                        data: values,
                        borderColor: 'rgb(34, 197, 94)',
                        backgroundColor: 'rgba(34, 197, 94, 0.1)',
                        fill: true,
                        tension: 0.3,
                        borderWidth: 2,
                        pointRadius: 0,
                        pointHoverRadius: 4
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    layout: {
                        padding: {
                            left: 10,
                            right: 30,
                            top: 10,
                            bottom: 5
                        }
                    },
                    plugins: {
                        legend: {
                            display: false
                        },
                        tooltip: {
                            enabled: true,
                            mode: 'index',
                            intersect: false,
                            position: 'edgeAware',
                            backgroundColor: 'rgba(30, 30, 30, 0.95)',
                            titleColor: '#fff',
                            bodyColor: '#fff',
                            titleFont: { size: 13, weight: 'bold' },
                            bodyFont: { size: 14 },
                            padding: 12,
                            cornerRadius: 8,
                            displayColors: false,
                            caretSize: 8,
                            caretPadding: 10,
                            callbacks: {
                                title: function(context) {
                                    return '⏰ ' + context[0].label;
                                },
                                label: function(context) {
                                    return '🔋 SOC: ' + context.parsed.y + '%';
                                }
                            }
                        }
                    },
                    scales: {
                        y: {
                            min: 0,
                            max: 100,
                            ticks: {
                                callback: value => value + '%',
                                stepSize: 20,
                                font: { size: 10 }
                            },
                            grid: {
                                color: 'rgba(200, 200, 200, 0.1)'
                            },
                            title: {
                                display: true,
                                text: 'Phần trăm (%)',
                                font: { size: 11 }
                            }
                        },
                        x: {
                            ticks: {
                                maxRotation: 0,
                                autoSkip: true,
                                autoSkipPadding: 30,
                                font: { size: 10 }
                            },
                            grid: {
                                display: false
                            }
                        }
                    },
                    interaction: {
                        mode: 'index',
                        axis: 'x',
                        intersect: false
                    },
                    hover: {
                        mode: 'index',
                        intersect: false
                    }
                }
            });
        }

        // Update SOC stats display
        updateSOCStats(currentSOC, maxSOC, minSOC, socHistory.length);
    }

    // Update SOC statistics display
    function updateSOCStats(current, max, min, dataPoints) {
        const currentEl = document.getElementById('soc-current');
        const maxEl = document.getElementById('soc-max');
        const minEl = document.getElementById('soc-min');
        const pointsEl = document.getElementById('soc-points');

        if (currentEl) currentEl.textContent = (typeof current === 'number' && (current > 0 || dataPoints > 0)) ? `${current}%` : '--%';
        if (maxEl) maxEl.textContent = (typeof max === 'number' && (max > 0 || dataPoints > 0)) ? `${max}%` : '--%';
        if (minEl) minEl.textContent = (typeof min === 'number' && (min > 0 || dataPoints > 0)) ? `${min}%` : '--%';
        if (pointsEl) pointsEl.textContent = dataPoints > 0 ? `${dataPoints}` : '0';
    }

    // Legacy function for SignalR SOC data (if API sends history)
    function updateSOCChart(data) {
        if (!data || !data.history) return;

        const ctx = document.getElementById('socChart');
        if (!ctx) return;

        // Convert API data to socHistory format
        socHistory = data.history.map(item => ({
            time: item.time,
            soc: item.soc,
            timestamp: Date.now()
        }));

        socDataReceived = true;
        updateSOCChartRealTime();
    }

    // ========================================
    // CHARTS
    // ========================================

    function updateCharts(data) {
        const timeLabels = generateTimeLabels();

        const processedData = {
            pv: processChartData(data.pv.tableValueInfo),
            batCharge: processBatteryChargingData(data.bat.tableValueInfo),
            batDischarge: processBatteryDischargingData(data.bat.tableValueInfo),
            load: processChartData(data.load.tableValueInfo),
            grid: processChartData(data.grid.tableValueInfo),
            essentialLoad: processChartData(data.essentialLoad.tableValueInfo)
        };

        const commonOptions = getCommonChartOptions();

        // PV Chart
        pvChart = createChart(pvChart, 'pvChart', 'Sản Lượng PV (W)', timeLabels, processedData.pv,
            'rgb(234, 179, 8)', 'rgba(234, 179, 8, 0.15)', commonOptions);

        // Battery Chart
        updateBatChart(timeLabels, processedData.batCharge, processedData.batDischarge, commonOptions);

        // Load Chart
        loadChart = createChart(loadChart, 'loadChart', 'Tải Tiêu Thụ (W)', timeLabels, processedData.load,
            'rgb(37, 99, 235)', 'rgba(37, 99, 235, 0.15)', commonOptions);

        // Grid Chart
        gridChart = createChart(gridChart, 'gridChart', 'Điện Lưới (W)', timeLabels, processedData.grid,
            'rgb(139, 92, 246)', 'rgba(139, 92, 246, 0.15)', commonOptions);

        // Essential Load Chart
        essentialChart = createChart(essentialChart, 'essentialChart', 'Tải Thiết Yếu (W)', timeLabels, processedData.essentialLoad,
            'rgb(75, 85, 99)', 'rgba(75, 85, 99, 0.15)', commonOptions);
    }

    function createChart(chartObj, canvasId, label, labels, data, borderColor, backgroundColor, options) {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return null;

        if (chartObj) chartObj.destroy();

        return new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: label,
                    data: data,
                    borderColor: borderColor,
                    backgroundColor: backgroundColor,
                    fill: true
                }]
            },
            options: options
        });
    }

    function updateBatChart(labels, chargeData, dischargeData, options) {
        const ctx = document.getElementById('batChart');
        if (!ctx) return;

        if (batChart) batChart.destroy();

        batChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'Sạc Pin (W)',
                        data: chargeData,
                        borderColor: 'rgb(22, 163, 74)',
                        backgroundColor: 'rgba(22, 163, 74, 0.15)',
                        fill: true
                    },
                    {
                        label: 'Xả Pin (W)',
                        data: dischargeData,
                        borderColor: 'rgb(220, 38, 38)',
                        backgroundColor: 'rgba(220, 38, 38, 0.15)',
                        fill: true
                    }
                ]
            },
            options: options
        });
    }

    function getCommonChartOptions() {
        return {
            responsive: true,
            maintainAspectRatio: false,
            elements: {
                point: { radius: 0, hoverRadius: 4 },
                line: { borderWidth: 2, tension: 0.2 }
            },
            plugins: {
                tooltip: {
                    mode: 'index',
                    intersect: false,
                    backgroundColor: 'rgba(50, 50, 50, 0.9)'
                },
                legend: {
                    position: 'top',
                    labels: { boxWidth: 12, padding: 10, font: { size: 11 } }
                }
            },
            scales: {
                x: {
                    ticks: { font: { size: 10 }, maxRotation: 0, autoSkip: true, autoSkipPadding: 30 },
                    grid: { display: true, color: 'rgba(200, 200, 200, 0.1)' }
                },
                y: {
                    beginAtZero: true,
                    ticks: {
                        font: { size: 10 },
                        callback: function (value) {
                            if (value >= 1000) return (value / 1000).toFixed(1) + 'k';
                            return value;
                        }
                    },
                    grid: { display: true, color: 'rgba(200, 200, 200, 0.1)' },
                    title: { display: true, text: 'Watt', font: { size: 11 } }
                }
            }
        };
    }

    // ========================================
    // DATA PROCESSING HELPERS
    // ========================================

    function generateTimeLabels() {
        const labels = [];
        for (let hour = 0; hour < 24; hour++) {
            for (let minute = 0; minute < 60; minute += 5) {
                labels.push(`${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`);
            }
        }
        return labels;
    }

    function processChartData(data) {
        return data ? [...data] : [];
    }

    function processBatteryChargingData(data) {
        if (!data) return [];
        return data.map(value => value < 0 ? Math.abs(value) : 0);
    }

    function processBatteryDischargingData(data) {
        if (!data) return [];
        return data.map(value => value > 0 ? value * -1 : 0);
    }

    // ========================================
    // UTILITY FUNCTIONS
    // ========================================

    function formatDate(date) {
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const day = String(date.getDate()).padStart(2, '0');
        return `${year}-${month}-${day}`;
    }

    function changeDate(offset) {
        const dateInput = document.getElementById('dateInput');
        if (!dateInput) return;

        let currentDate = new Date(dateInput.value);
        currentDate.setDate(currentDate.getDate() + offset);
        dateInput.value = formatDate(currentDate);
        fetchData();
    }

    function scrollToElement(elementId) {
        const element = document.getElementById(elementId);
        if (element) {
            element.scrollIntoView({ behavior: 'smooth' });
        }
    }

    function showElement(elementId) {
        const element = document.getElementById(elementId);
        if (element) {
            element.classList.remove('hidden');
        }
    }

    function updateValue(elementId, value) {
        const element = document.getElementById(elementId);
        if (element) {
            const oldValue = previousValues[elementId];
            const newValue = String(value);

            // Only blink if value actually changed
            if (oldValue !== newValue) {
                element.textContent = value;
                element.classList.remove('value-updated');
                // Force reflow to restart animation
                void element.offsetWidth;
                element.classList.add('value-updated');
                previousValues[elementId] = newValue;

                // Remove class after animation completes
                setTimeout(() => element.classList.remove('value-updated'), 600);
            }
        }
    }

    // Update value with innerHTML and blink effect
    function updateValueHTML(elementId, html) {
        const element = document.getElementById(elementId);
        if (element) {
            const oldHTML = previousValues[elementId + '_html'];
            const newHTML = String(html);

            // Only blink if value actually changed
            if (oldHTML !== newHTML) {
                element.innerHTML = html;
                element.classList.remove('value-updated');
                void element.offsetWidth;
                element.classList.add('value-updated');
                previousValues[elementId + '_html'] = newHTML;

                setTimeout(() => element.classList.remove('value-updated'), 600);
            }
        }
    }

    function updateFlowStatus(flowId, isActive) {
        const flow = document.getElementById(flowId);
        if (flow) {
            if (isActive) {
                flow.classList.remove('inactive');
                flow.classList.add('active');
            } else {
                flow.classList.add('inactive');
                flow.classList.remove('active');
            }
        }
    }

    function showLoading(show) {
        const loading = document.getElementById('loading');
        if (loading) {
            loading.classList.toggle('hidden', !show);
        }
    }

    function showError(message) {
        const errorDiv = document.getElementById('errorMessage');
        const errorText = document.getElementById('errorText');
        if (errorDiv && errorText) {
            errorText.textContent = message;
            errorDiv.classList.remove('hidden');
        }
    }

    function hideError() {
        const errorDiv = document.getElementById('errorMessage');
        if (errorDiv) {
            errorDiv.classList.add('hidden');
        }
    }

    // ========================================
    // AUTO REFRESH
    // ========================================

    setInterval(() => {
        const deviceId = document.getElementById('deviceId')?.value?.trim();
        if (deviceId) {
            console.log("Auto-refreshing data");
            fetchData();
        }
    }, 5 * 60 * 1000); // 5 minutes

    // Listen for theme changes
    const observer = new MutationObserver(() => {
        configureChartDefaults();
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
});
