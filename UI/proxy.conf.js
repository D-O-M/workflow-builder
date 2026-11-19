const apiProxyTarget = process.env.WORKFLOW_BUILDER_API_PROXY_TARGET || 'http://localhost:5180';
const signalRProxyTarget = process.env.WORKFLOW_BUILDER_SIGNALR_PROXY_TARGET || 'http://localhost:5181';

module.exports = {
  '^/management(?:/|$)': {
    target: 'http://127.0.0.1:' + (process.env.WORKFLOW_BUILDER_MANAGEMENT_PORT || '4201'),
    changeOrigin: true,
    ws: true,
  },
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
