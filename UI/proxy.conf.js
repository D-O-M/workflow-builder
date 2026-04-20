const apiProxyTarget = process.env.WORKFLOW_BUILDER_API_PROXY_TARGET || 'http://localhost:5180';
const signalRProxyTarget = process.env.WORKFLOW_BUILDER_SIGNALR_PROXY_TARGET || 'http://localhost:5181';

module.exports = {
  '/api': {
    target: apiProxyTarget,
    changeOrigin: true,
    secure: false,
    ws: true,
  },
  '/hubs': {
    target: signalRProxyTarget,
    changeOrigin: true,
    secure: false,
    ws: true,
  },
};
