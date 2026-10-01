import type { NextConfig } from "next";

const API_PROXY_TARGET = process.env.API_PROXY_TARGET ?? "http://127.0.0.1:8080";

const nextConfig: NextConfig = {
  output: 'standalone', // Required by the Replit Deployment build/run scripts in .replit
  turbopack: {
    root: process.cwd(),
  },
  allowedDevOrigins: [
    process.env.REPLIT_DEV_DOMAIN ?? '',
    '*.replit.dev',
    '*.kirk.replit.dev',
  ].filter(Boolean),
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