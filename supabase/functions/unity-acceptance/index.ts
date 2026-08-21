Deno.serve(async (request: Request) => {
  if (request.method !== "POST") {
    return new Response(JSON.stringify({ error: "Method not allowed" }), {
      status: 405,
      headers: { "content-type": "application/json" },
    });
  }

  let payload: { run_id?: string };
  try {
    payload = await request.json();
  } catch {
    return new Response(JSON.stringify({ error: "A JSON body is required" }), {
      status: 400,
      headers: { "content-type": "application/json" },
    });
  }

  const runId = payload.run_id?.trim();
  if (!runId) {
    return new Response(JSON.stringify({ error: "run_id is required" }), {
      status: 400,
      headers: { "content-type": "application/json" },
    });
  }

  return new Response(JSON.stringify({ accepted: true, run_id: runId }), {
    status: 200,
    headers: { "content-type": "application/json" },
  });
});
