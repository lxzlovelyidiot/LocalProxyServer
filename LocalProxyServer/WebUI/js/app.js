let currentWs = null;

const Pages = {
    async renderOverview(container) {
        document.getElementById('page-title').innerText = 'Overview';
        try {
            const status = await API.getStatus();
            container.innerHTML = `
                <div class="dashboard-grid">
                    <div class="card">
                        <h2 class="card-title">Proxy Service</h2>
                        <p>Status: ${UI.renderStatusBadge(status.proxy.status)}</p>
                        <p>Port: ${status.proxy.port || 8080}</p>
                        <p>HTTPS: ${status.proxy.useHttps ? 'Enabled' : 'Disabled'}</p>
                        <p>Active Upstreams: ${status.proxy.upstreamCount}</p>
                    </div>
                    <div class="card">
                        <h2 class="card-title">DNS Service</h2>
                        <p>Status: ${UI.renderStatusBadge(status.dns.status)}</p>
                        <p>Port: ${status.dns.port || 53}</p>
                    </div>
                    <div class="card">
                        <h2 class="card-title">Certificate</h2>
                        <p>Subject: ${status.certificate.subject || 'N/A'}</p>
                        <p>Valid Until: ${status.certificate.notAfter ? new Date(status.certificate.notAfter).toLocaleString() : 'N/A'}</p>
                        <p>Installed: ${UI.renderStatusBadge(status.certificate.isInstalled)}</p>
                    </div>
                </div>
            `;
        } catch (e) {
            UI.showToast('Failed to load overview: ' + e.message, 'error');
        }
    },

    async renderProxy(container) {
        document.getElementById('page-title').innerText = 'Proxy Configuration';
        try {
            const cfg = await API.getProxyConfig();
            container.innerHTML = `
                <div class="card" style="max-width: 600px">
                    <div class="form-group">
                        <label class="form-label">Port</label>
                        <input type="number" id="proxy-port" class="form-control" value="${cfg.port}">
                    </div>
                    <div class="form-group">
                        <label class="form-label">Use HTTPS</label>
                        <select id="proxy-https" class="form-control">
                            <option value="true" ${cfg.useHttps ? 'selected' : ''}>Enabled</option>
                            <option value="false" ${!cfg.useHttps ? 'selected' : ''}>Disabled</option>
                        </select>
                    </div>
                    <div class="form-group">
                        <label class="form-label">CRL Port</label>
                        <input type="number" id="proxy-crlport" class="form-control" value="${cfg.crlPort}">
                    </div>
                    <div class="form-group">
                        <label class="form-label">Load Balancing Strategy</label>
                        <select id="proxy-lb" class="form-control">
                            <option value="roundRobin" ${cfg.loadBalancingStrategy === 'roundRobin' ? 'selected' : ''}>Round Robin</option>
                            <option value="random" ${cfg.loadBalancingStrategy === 'random' ? 'selected' : ''}>Random</option>
                        </select>
                    </div>
                    <button id="btn-save-proxy" class="btn btn-primary">Save Changes</button>
                    ${UI.renderWarningRestart()}
                    <div style="margin-top:20px; color:var(--text-secondary)">
                        <p>Note: Upstreams are managed in the Upstreams tab.</p>
                    </div>
                </div>
            `;
            document.getElementById('btn-save-proxy').onclick = async () => {
                const newCfg = {
                    port: parseInt(document.getElementById('proxy-port').value),
                    useHttps: document.getElementById('proxy-https').value === 'true',
                    crlPort: parseInt(document.getElementById('proxy-crlport').value),
                    loadBalancingStrategy: document.getElementById('proxy-lb').value
                };
                try {
                    await API.updateProxyConfig(newCfg);
                    UI.showToast('Configuration saved.');
                } catch (e) { UI.showToast(e.message, 'error'); }
            };
        } catch (e) { UI.showToast(e.message, 'error'); }
    },

    async renderUpstreams(container) {
        document.getElementById('page-title').innerText = 'Upstreams Management';

        const renderTable = async () => {
            try {
                const upstreams = await API.getUpstreams();
                let rows = '';
                upstreams.forEach((u, ix) => {
                    rows += `
                    <tr>
                        <td>#${ix}</td>
                        <td>${UI.renderStatusBadge(u.enabled)}</td>
                        <td>${u.type}</td>
                        <td>${u.host}:${u.port}</td>
                        <td>${UI.renderStatusBadge(u.processRunning)} ${u.processRunning ? `(PID: ${u.processId})` : ''}</td>
                        <td>${u.healthCheckEnabled ? UI.renderStatusBadge(u.lastHealthCheckResult) : 'N/A'}</td>
                        <td>
                            <button class="btn btn-warning btn-sm" onclick="editUpstream(${ix})">Edit</button>
                            <button class="btn btn-danger btn-sm" onclick="deleteUpstream(${ix})">Delete</button>
                        </td>
                    </tr>`;
                });

                container.innerHTML = `
                    <div class="card">
                        <div style="margin-bottom:16px"><button id="btn-add-upstream" class="btn btn-primary">Add Upstream</button></div>
                        <div class="table-wrapper">
                            <table class="table">
                                <thead>
                                    <tr>
                                        <th>#</th>
                                        <th>Enabled</th>
                                        <th>Type</th>
                                        <th>Endpoint</th>
                                        <th>Process</th>
                                        <th>Health</th>
                                        <th>Actions</th>
                                    </tr>
                                </thead>
                                <tbody>${rows || '<tr><td colspan="7">No upstreams configured</td></tr>'}</tbody>
                            </table>
                        </div>
                    </div>
                `;

                document.getElementById('btn-add-upstream').onclick = () => Pages.showUpstreamModal(container);
            } catch (e) { UI.showToast(e.message, 'error'); }
        };

        window.editUpstream = async (ix) => {
            try {
                const config = await API.getUpstream(ix);
                Pages.showUpstreamModal(container, config, ix);
            } catch (e) { UI.showToast(e.message, 'error'); }
        };

        window.deleteUpstream = async (ix) => {
            if (confirm('Are you sure you want to delete this upstream?')) {
                try {
                    await API.deleteUpstream(ix);
                    UI.showToast('Deleted');
                    await renderTable();
                } catch (e) { UI.showToast(e.message, 'error'); }
            }
        };

        await renderTable();
    },
    showUpstreamModal(container, config = null, index = -1) {
        const isEdit = index >= 0;
        const modal = document.createElement('div');
        modal.className = 'modal-overlay';

        const cfg = config || {
            type: 'socks5',
            host: '127.0.0.1',
            port: 1080,
            enabled: true,
            process: { autoStart: false, fileName: '', arguments: '', workingDirectory: '' },
            healthCheck: { enabled: false, intervalMs: 30000, timeoutMs: 5000, failureThreshold: 3 }
        };

        modal.innerHTML = `
            <div class="modal-content">
                <div class="modal-header">
                    <h3>${isEdit ? 'Edit Upstream' : 'Add New Upstream'}</h3>
                    <button class="btn" style="background:none; font-size:1.5rem;" onclick="this.closest('.modal-overlay').remove()">×</button>
                </div>
                <div class="modal-body">
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Type</label>
                            <select id="up-type" class="form-control">
                                <option value="socks5" ${cfg.type === 'socks5' ? 'selected' : ''}>SOCKS5</option>
                                <option value="http" ${cfg.type === 'http' ? 'selected' : ''}>HTTP</option>
                                <option value="direct" ${cfg.type === 'direct' ? 'selected' : ''}>Direct</option>
                                <option value="daemon" ${cfg.type === 'daemon' ? 'selected' : ''}>Daemon</option>
                            </select>
                        </div>
                        <div class="form-group" style="flex:2">
                            <label class="form-label">Host</label>
                            <input type="text" id="up-host" class="form-control" value="${cfg.host || ''}" placeholder="127.0.0.1">
                        </div>
                        <div class="form-group">
                            <label class="form-label">Port</label>
                            <input type="number" id="up-port" class="form-control" value="${cfg.port || ''}" placeholder="1080">
                        </div>
                    </div>

                    <div class="form-group">
                        <label class="checkbox-group">
                            <input type="checkbox" id="up-enabled" ${cfg.enabled ? 'checked' : ''}> Enabled
                        </label>
                    </div>

                    <div class="section-divider"><span>Process Management</span></div>
                    <div class="form-group">
                        <label class="checkbox-group">
                            <input type="checkbox" id="up-proc-auto" ${cfg.process?.autoStart ? 'checked' : ''}> Auto-start upstream process
                        </label>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Executable Path</label>
                        <input type="text" id="up-proc-file" class="form-control" value="${cfg.process?.fileName || ''}" placeholder="C:\\path\\to\\proxy.exe">
                    </div>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Arguments</label>
                            <input type="text" id="up-proc-args" class="form-control" value="${cfg.process?.arguments || ''}" placeholder="-c config.json">
                        </div>
                    </div>

                    <div class="section-divider"><span>Health Check</span></div>
                    <div class="form-group">
                        <label class="checkbox-group">
                            <input type="checkbox" id="up-hc-enabled" ${cfg.healthCheck?.enabled ? 'checked' : ''}> Enable Periodic Health Check
                        </label>
                    </div>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Interval (ms)</label>
                            <input type="number" id="up-hc-interval" class="form-control" value="${cfg.healthCheck?.intervalMs || 30000}">
                        </div>
                        <div class="form-group">
                            <label class="form-label">Timeout (ms)</label>
                            <input type="number" id="up-hc-timeout" class="form-control" value="${cfg.healthCheck?.timeoutMs || 5000}">
                        </div>
                        <div class="form-group">
                            <label class="form-label">Fail Threshold</label>
                            <input type="number" id="up-hc-threshold" class="form-control" value="${cfg.healthCheck?.failureThreshold || 3}">
                        </div>
                    </div>
                </div>
                <div class="modal-footer">
                    <button class="btn btn-secondary" onclick="this.closest('.modal-overlay').remove()">Cancel</button>
                    <button id="btn-modal-save" class="btn btn-primary">Save Upstream</button>
                </div>
            </div>
        `;

        document.body.appendChild(modal);

        document.getElementById('btn-modal-save').onclick = async () => {
            const data = {
                type: document.getElementById('up-type').value,
                host: document.getElementById('up-host').value,
                port: parseInt(document.getElementById('up-port').value),
                enabled: document.getElementById('up-enabled').checked,
                process: {
                    autoStart: document.getElementById('up-proc-auto').checked,
                    fileName: document.getElementById('up-proc-file').value,
                    arguments: document.getElementById('up-proc-args').value,
                    redirectOutput: true,
                    autoRestart: true
                },
                healthCheck: {
                    enabled: document.getElementById('up-hc-enabled').checked,
                    intervalMs: parseInt(document.getElementById('up-hc-interval').value),
                    timeoutMs: parseInt(document.getElementById('up-hc-timeout').value),
                    failureThreshold: parseInt(document.getElementById('up-hc-threshold').value)
                }
            };

            try {
                if (isEdit) await API.updateUpstream(index, data);
                else await API.addUpstream(data);

                UI.showToast(isEdit ? 'Updated' : 'Added');
                modal.remove();
                await Pages.renderUpstreams(container);
            } catch (e) { UI.showToast(e.message, 'error'); }
        };
    },
    async renderDns(container) {
        document.getElementById('page-title').innerText = 'DNS Configuration';
        try {
            const cfg = await API.getDnsConfig();
            container.innerHTML = `
                <div class="card" style="max-width: 600px">
                    <div class="form-group">
                        <label class="form-label">Enabled</label>
                        <select id="dns-enabled" class="form-control">
                            <option value="true" ${cfg.enabled ? 'selected' : ''}>Enabled</option>
                            <option value="false" ${!cfg.enabled ? 'selected' : ''}>Disabled</option>
                        </select>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Port</label>
                        <input type="number" id="dns-port" class="form-control" value="${cfg.port}">
                    </div>
                    <button id="btn-save-dns" class="btn btn-primary">Save Changes</button>
                    ${UI.renderWarningRestart()}
                </div>
            `;
            document.getElementById('btn-save-dns').onclick = async () => {
                const newCfg = {
                    ...cfg,
                    enabled: document.getElementById('dns-enabled').value === 'true',
                    port: parseInt(document.getElementById('dns-port').value)
                };
                try {
                    await API.updateDnsConfig(newCfg);
                    UI.showToast('Configuration saved.');
                } catch (e) { UI.showToast(e.message, 'error'); }
            };
        } catch (e) { UI.showToast(e.message, 'error'); }
    },
    async renderCertificate(container) {
        document.getElementById('page-title').innerText = 'Certificate Management';
        try {
            const cert = await API.getCertificate();
            container.innerHTML = `
                <div class="card" style="max-width: 600px">
                    <div class="form-group">
                        <label class="form-label">Subject</label>
                        <input type="text" class="form-control" value="${cert.subject || ''}" disabled>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Issuer</label>
                        <input type="text" class="form-control" value="${cert.issuer || ''}" disabled>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Thumbprint</label>
                        <input type="text" class="form-control" value="${cert.thumbprint || ''}" disabled>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Is Installed to Root CA</label>
                        <p>${UI.renderStatusBadge(cert.isInstalled)}</p>
                    </div>
                    <div style="margin-top: 16px;">
                        <button id="btn-regen-cert" class="btn btn-warning">Regenerate Root CA</button>
                    </div>
                </div>
            `;

            document.getElementById('btn-regen-cert').onclick = async () => {
                if (confirm('Are you sure? This requires proxy restart and re-installing the CA in your OS.')) {
                    try {
                        await API.regenerateCertificate();
                        UI.showToast('Regenerated. You must restart the proxy.');
                        await Pages.renderCertificate(container);
                    } catch (e) { UI.showToast(e.message, 'error'); }
                }
            };
        } catch (e) { UI.showToast(e.message, 'error'); }
    },
    async renderLogs(container) {
        document.getElementById('page-title').innerText = 'Live Logs';
        container.innerHTML = `
            <div class="card">
                <div style="margin-bottom:12px; display:flex; gap:12px; align-items:center;">
                    <label>Level:</label>
                    <select id="log-level" class="form-control" style="width:150px">
                        <option value="Debug">Debug</option>
                        <option value="Information" selected>Info</option>
                        <option value="Warning">Warning</option>
                        <option value="Error">Error</option>
                    </select>
                    <button id="btn-clear-logs" class="btn btn-warning">Clear</button>
                    <button id="btn-toggle-logs" class="btn btn-primary">Pause</button>
                    <span id="log-status" style="color:var(--success-color)">Connected</span>
                </div>
                <div id="log-output" class="log-container"></div>
            </div>
        `;

        const logOutput = document.getElementById('log-output');
        let isPaused = false;

        document.getElementById('btn-clear-logs').onclick = () => logOutput.innerHTML = '';
        document.getElementById('btn-toggle-logs').onclick = (e) => {
            isPaused = !isPaused;
            e.target.innerText = isPaused ? 'Resume' : 'Pause';
        };

        const connectWs = () => {
            if (currentWs) currentWs.close();

            // Clear old logs from the screen when changing the connection
            if (logOutput) {
                logOutput.innerHTML = '';
            }

            const level = document.getElementById('log-level').value;
            const wsUrl = `wss://${location.host}/ws/logs?level=${level}`;
            const ws = new WebSocket(wsUrl);
            currentWs = ws;

            const ls = document.getElementById('log-status');
            if (ls) {
                ls.innerText = 'Connecting...';
                ls.style.color = 'var(--warning-color)';
            }

            ws.onopen = () => {
                if (currentWs === ws && ls) {
                    ls.innerText = 'Connected';
                    ls.style.color = 'var(--success-color)';
                }
            };

            ws.onmessage = (e) => {
                if (isPaused) return;
                const log = JSON.parse(e.data);
                const div = document.createElement('div');
                div.className = 'log-entry';
                div.innerHTML = `
                    <span class="log-time">[${new Date(log.timestamp).toLocaleTimeString()}]</span>
                    <span class="log-level-${log.level}">[${log.level}]</span>
                    <span class="log-category">[${log.category}]</span>
                    <span class="log-msg">${escapeHtml(log.message)}</span>
                `;
                logOutput.appendChild(div);
                if (logOutput.childNodes.length > 1000) logOutput.removeChild(logOutput.firstChild);
                logOutput.scrollTop = logOutput.scrollHeight;
            };

            ws.onclose = () => {
                if (currentWs === ws && ls) {
                    ls.innerText = 'Disconnected';
                    ls.style.color = 'var(--danger-color)';
                }
            };
        };

        document.getElementById('log-level').onchange = connectWs;
        connectWs();
    }
};

async function handleNavigation(pageId) {
    if (currentWs) { currentWs.close(); currentWs = null; }

    document.querySelectorAll('.nav-item').forEach(el => el.classList.remove('active'));
    const link = document.querySelector(`.nav-item[data-page="${pageId}"]`);
    if (link) link.classList.add('active');

    const container = document.getElementById('pages-container');
    container.innerHTML = '<div style="text-align:center;padding:40px;color:var(--text-secondary)">Loading...</div>';

    if (pageId === 'overview') await Pages.renderOverview(container);
    else if (pageId === 'proxy') await Pages.renderProxy(container);
    else if (pageId === 'upstreams') await Pages.renderUpstreams(container);
    else if (pageId === 'dns') await Pages.renderDns(container);
    else if (pageId === 'logs') await Pages.renderLogs(container);
    else if (pageId === 'certificate') await Pages.renderCertificate(container);
}

document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('.nav-item').forEach(el => {
        el.addEventListener('click', (e) => {
            e.preventDefault();
            const pageId = el.getAttribute('data-page');
            window.location.hash = pageId;
            handleNavigation(pageId);
        });
    });

    const initialPage = window.location.hash.replace('#', '') || 'overview';
    handleNavigation(initialPage);

    // Theme Management
    const themeBtn = document.getElementById('btn-toggle-theme');
    const setLight = (isLight) => {
        if (isLight) document.documentElement.setAttribute('data-theme', 'light');
        else document.documentElement.removeAttribute('data-theme');
        localStorage.setItem('theme', isLight ? 'light' : 'dark');
    };

    // Init theme
    const savedTheme = localStorage.getItem('theme');
    if (savedTheme === 'light') setLight(true);

    if (themeBtn) {
        themeBtn.onclick = () => {
            const isLight = document.documentElement.getAttribute('data-theme') !== 'light';
            setLight(isLight);
        };
    }

    // Global Actions
    document.getElementById('btn-restart-proxy').onclick = async () => {
        try { await API.restartProxy(); UI.showToast('Upstreams restarted'); }
        catch (e) { UI.showToast(e.message, 'error'); }
    };

    document.getElementById('btn-restart-dns').onclick = async () => {
        try { await API.stopDns(); await API.startDns(); UI.showToast('DNS restarted'); }
        catch (e) { UI.showToast(e.message, 'error'); }
    };
});
