> ⚠️ **归属声明（2026-09-16）**：本调研的目标是 **DeepSeek Harness（DSH）的联网搜索能力，不是 RiMCP**。
> RiMCP 明确不做联网/出网功能（见 docs/rag-assessment-2026-09.md §5）。本文件仅作技术存档，供 DSH 侧会话取用。

# Adding web search + GitHub code search to the .NET 8 stdio RimWorld RAG MCP server (2026)

Verification note: `[live]` = endpoint hit from this machine during research. `[UNCERTAIN]` = not live-verified (web-search engines were rate-limited when this was written) or vendor-claim only. All endpoints are plain HTTP/JSON and callable from .NET with `HttpClient`.

## 1. GitHub code search

| Option | Auth | Limits |
|---|---|---|
| REST `GET /search/code` | **Required** | 10 req/min, **1000-result hard cap** |
| GraphQL `search(type: CODE)` | Required | 5000 points/h, same 1000 cap |
| `gh search code` CLI | Required | same API, `--limit ≤ 1000` |

- `[live]` `https://api.github.com/search/code?q=ThingDef` without a token → `401 {"message":"Requires authentication"}`. Unauthenticated `/rate_limit` reports `search:{limit:10}`; unauthenticated GraphQL limit is 0.
- The 1000-result cap is not paginable past, and search covers the **default branch only** (large/generated files excluded) — it is not a full-text engine over all public repos. **There is no bulk or streaming API.** Docs: [search/code](https://docs.github.com/en/rest/search/search), [rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api), [GraphQL limits](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api).
- Bulk-ish paths: BigQuery `bigquery-public-data.github_repos` (SQL over file contents; 1 TB/mo free then ~$6.25/TB, **stale snapshot**) and GH Archive (`githubarchive`; hourly events, **no file contents at all**). `[UNCERTAIN]` current BigQuery pricing/freshness.

## 2. Third-party code-search alternatives

- **grep.app** — free, no key, `GET https://grep.app/api/search?q=`; Vercel acquired it ([blog](https://vercel.com/blog/vercel-acquires-grep)) and the API now sits behind a Vercel Security Checkpoint: `[live]` HTTP 429 challenge HTML. **Not usable server-side** without a headless browser.
- **Sourcegraph** — advertises free public search over 2M+ OSS repos; `[live]` `/.api/graphql` and `/.api/search/stream` returned **403 "Firewall Block"** from this host. No documented public API contract (focus shifted to Amp). `[UNCERTAIN]` whether an authenticated token would work.
- **Gitingest** (`gitingest.com`, open-source, self-hostable) — turns one repo into a text digest. Useful ingestion for the existing index; **not** a search engine.
- **DeepWiki** (`deepwiki.com`) — free AI wiki per public repo; good for "explain this mod's architecture". Not full-text code search. `[UNCERTAIN]` official API/MCP.
- Codeberg/other Gitea mirrors index only their own hosts (no RimWorld coverage). `searchcode.com`'s documented API path returned `[live]` 404 — treat as unreliable.

## 3. Web search APIs

| Provider | Key | Free tier / cost |
|---|---|---|
| **Brave** | yes | $5 per 1k requests, $5 free credits/mo, 50 rps ([pricing](https://api-dashboard.search.brave.com/documentation/pricing)) |
| **Tavily** | yes | 1,000 credits/mo free; then $0.008/credit (1 credit = 1 basic search) ([docs](https://docs.tavily.com/documentation/api-credits)) |
| **Exa** | yes | $20 signup + $10/mo free credits; search $7/1k ([pricing](https://exa.ai/docs/admin/pricing)) |
| **Serper** (Google SERP) | yes | 2,500 free queries, then ~$1/1k — unofficial proxy `[UNCERTAIN]` |
| **SerpApi** | yes | 250 searches/mo free, 50/hour `[live]` |
| Google Programmable Search | yes (+`cx`) | 100 queries/day free, $5/1k, 10k/day cap `[UNCERTAIN]` |
| Bing Web Search | — | **retired 11 Aug 2025** — do not build on it |
| DuckDuckGo html/lite | no | free, unofficial, bot-blocked, ToS-unclear |
| **SearXNG** (self-hosted) | no | infra cost only; JSON output disabled by default for non-local callers (`search.formats`) |

Also agent-oriented: Firecrawl (search + markdown in one call), Linkup, Perplexity Sonar.

## 4. Drop-in MCP servers for search

- **GitHub official** — remote `https://api.githubcopilot.com/mcp/` (`[live]` 401 → OAuth/PAT required) + Docker `ghcr.io/github/github-mcp-server`; MIT; exposes `search_code`.
- **Tavily official** — remote `https://mcp.tavily.com/mcp/?tavilyApiKey=…` and `npx tavily-mcp`; `tavily-mcp@0.2.22`, MIT.
- **Brave official** — `@brave/brave-search-mcp-server@2.1.3`, MIT.
- **Exa** — `exa-mcp-server@3.4.1`; **Firecrawl** — `firecrawl-mcp@3.24.0`, MIT.
- Legacy reference servers `@modelcontextprotocol/server-github@2025.4.8` and `…/server-brave-search@0.6.2` (MIT) are superseded.
- Transports: stdio + Streamable HTTP (SSE deprecated). The .NET `ModelContextProtocol` NuGet (GA since 1.0.0; current **2.2.0**) ships both client and server, so this server can act as an MCP **client** to aggregate a vendor server — or simply call the vendor's REST API, avoiding a Node dependency.

## 5. RimWorld-specific value and blockers

1. **General web search** — one tool covers Steam Workshop pages, mod GitHub READMEs, Reddit, forums, wiki mirrors. Highest marginal value.
2. **Steam Workshop** — `ISteamRemoteStorage/GetPublishedFileDetails/v1` is **keyless** (`[live]` HTTP 200; returns title/description/subscriptions/time_updated per ID). `IPublishedFileService/QueryFiles/v1` needs a free key ([dev/apikey](https://steamcommunity.com/dev/apikey)) to browse appid **294100**.
3. **GitHub code search** — moderate: 1000 results / 10 rpm, and the local index already has decompiled vanilla. Better use: clone a mod repo into the existing RAG pipeline.
4. **RimWorld wiki** — best content, worst access: `rimworldwiki.com/api.php` and `/rest.php` both return `[live]` **403 Cloudflare "Just a moment…"**. Reach it via a search API's extraction, never direct scraping.

Hard blockers: those three verified 403/429 walls; Bing's retirement; GitHub ToS limits on automated use and data redistribution; **licensing** — GitHub grants no code licence and most RimWorld mod repos have no LICENSE file, so index locally but never redistribute fetched code; Workshop mods are code-unlicensed.

**Conclusion:** for a RimWorld modding assistant, the cheapest high-value move is to add **one web-search tool (Brave or Tavily) behind a single API key** — `HttpClient` + JSON, one stdio tool — then add the keyless Steam Workshop lookup; skip GitHub code search and instead clone interesting mod repos into the local index.
