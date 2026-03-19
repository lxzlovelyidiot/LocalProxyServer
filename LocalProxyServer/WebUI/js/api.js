const API = {
    async getStatus() { return await this.request('/api/status'); },
    async getProxyConfig() { return await this.request('/api/config/proxy'); },
    async updateProxyConfig(data) { return await this.request('/api/config/proxy', 'PUT', data); },
    async getUpstreams() { return await this.request('/api/config/proxy/upstreams'); },
    async addUpstream(data) { return await this.request('/api/config/proxy/upstreams', 'POST', data); },
    async updateUpstream(index, data) { return await this.request(`/api/config/proxy/upstreams/${index}`, 'PUT', data); },
    async deleteUpstream(index) { return await this.request(`/api/config/proxy/upstreams/${index}`, 'DELETE'); },
    async getDnsConfig() { return await this.request('/api/config/dns'); },
    async updateDnsConfig(data) { return await this.request('/api/config/dns', 'PUT', data); },
    async startProxy() { return await this.request('/api/proxy/start', 'POST'); },
    async stopProxy() { return await this.request('/api/proxy/stop', 'POST'); },
    async restartProxy() { return await this.request('/api/proxy/restart', 'POST'); },
    async startDns() { return await this.request('/api/dns/start', 'POST'); },
    async stopDns() { return await this.request('/api/dns/stop', 'POST'); },
    async getCertificate() { return await this.request('/api/certificate'); },
    async regenerateCertificate() { return await this.request('/api/certificate/regenerate', 'POST'); },
    
    async request(url, method = 'GET', body = null) {
        const options = { method, headers: { 'Content-Type': 'application/json' } };
        if (body) options.body = JSON.stringify(body);
        const res = await fetch(url, options);
        if (!res.ok) throw new Error(`HTTP ${res.status} ${res.statusText}`);
        return await res.json();
    }
};
