const UI = {
    showToast(message, type = 'success') {
        const container = document.getElementById('toast-container');
        const toast = document.createElement('div');
        toast.className = `toast ${type}`;
        toast.innerHTML = `<span>${message}</span> <button style="background:none;border:none;color:white;cursor:pointer" onclick="this.parentElement.remove()">✕</button>`;
        container.appendChild(toast);
        setTimeout(() => toast.remove(), 5000);
    },
    
    renderStatusBadge(status) {
        if (status === null || status === undefined) return '<span style="color:var(--text-secondary)">Unknown</span>';
        const isRunning = status === 'Running' || status === true;
        const sClass = isRunning ? 'status-running' : (status === 'Stopped' || status === false ? 'status-stopped' : 'status-error');
        return `<span class="status-indicator ${sClass}"><span class="status-dot"></span>${status === true ? 'Enabled' : (status === false ? 'Disabled' : status)}</span>`;
    },
    
    renderWarningRestart() {
        return `<div style="margin-top:12px; font-size:0.875rem; color:var(--warning-color)">
            ⚠️ Changes require service restart to take effect.
        </div>`;
    }
};

function escapeHtml(unsafe) {
    if (!unsafe) return '';
    return unsafe.toString()
         .replace(/&/g, "&amp;")
         .replace(/</g, "&lt;")
         .replace(/>/g, "&gt;")
         .replace(/"/g, "&quot;")
         .replace(/'/g, "&#039;");
}
