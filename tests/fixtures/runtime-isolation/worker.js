/* If worker-src is correctly enforced, this local worker must never start. */
self.onmessage = async function (event) {
  const base = event.data && event.data.base;
  if (typeof base !== "string" || !/^http:\/\/127\.0\.0\.1:\d+\/[a-f0-9]{32}$/.test(base)) return;
  try { const response = await fetch(base + "/probe/worker-fetch"); self.postMessage({ result: "response", status: response.status }); }
  catch (error) { self.postMessage({ result: error.name }); }
};
