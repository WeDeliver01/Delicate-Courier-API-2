import type { NextConfig } from "next";

const API_PROXY_TARGET = process.env.API_PROXY_TARGET ?? "http://127.0.0.1:8080";

const nextConfig: NextConfig = {
  // Standalone output: the VPS runs `node server.js` from .next/standalone under systemd,
  // behind nginx. Removing this breaks the deploy, not just the build.
  output: 'standalone',
  turbopack: {
    root: process.cwd(),
  },
  async rewrites() {
    return [
      { source: '/api/:path*', destination: `${API_PROXY_TARGET}/api/:path*` },
      { source: '/swagger', destination: `${API_PROXY_TARGET}/swagger` },
      { source: '/swagger/:path*', destination: `${API_PROXY_TARGET}/swagger/:path*` },
      { source: '/hangfire', destination: `${API_PROXY_TARGET}/hangfire` },
      { source: '/hangfire/:path*', destination: `${API_PROXY_TARGET}/hangfire/:path*` },
      { source: '/health', destination: `${API_PROXY_TARGET}/health` },
      { source: '/alive', destination: `${API_PROXY_TARGET}/alive` },
    ];
  },
};

export default nextConfig;