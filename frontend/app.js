let apiBase = localStorage.getItem("lensee.apiBase") || "http://localhost:5000";
const authKey = "lensee.auth";
const apiCandidates = ["http://localhost:5275", "http://localhost:5000", "https://localhost:7237"];
const seededCredentials = [
  { username: "admin", password: "Admin123!", role: "Admin" },
  { username: "clevel", password: "CLevel123!", role: "C-Level" },
  { username: "accountant", password: "Accountant123!", role: "Accountant" },
  { username: "roxy_clerk", password: "Clerk123!", role: "Roxy clerk" },
  { username: "retail_clerk", password: "Clerk123!", role: "Retail clerk" },
  { username: "online_clerk", password: "Clerk123!", role: "Online clerk" }
];

const routes = {
  "/login": { title: "Sign In", label: "Identity", roles: [], render: renderLogin },
  "/dashboard": { title: "Operations Console", label: "Dashboard", roles: ["CLevel", "Admin", "Accountant", "WarehouseClerk"], render: renderDashboard },
  "/catalog": { title: "Catalog", label: "Catalog", roles: ["CLevel", "Admin", "WarehouseClerk"], render: () => renderListPattern("Catalog", ["Category", "Brand", "Product", "Status"]) },
  "/inventory": { title: "Inventory", label: "Inventory", roles: ["CLevel", "Admin", "WarehouseClerk"], render: renderInventory },
  "/crm": { title: "CRM", label: "CRM", roles: ["Admin"], render: () => renderListPattern("Merchants", ["Business", "Contact", "Status", "Updated"]) },
  "/operations": { title: "Operations", label: "Operations", roles: ["CLevel", "Admin", "Accountant", "WarehouseClerk"], render: renderOperations },
  "/payments": { title: "Payments", label: "Payments", roles: ["CLevel", "Admin", "Accountant"], render: () => renderListPattern("Payment Logs", ["Merchant", "Amount", "Status", "Owner"]) },
  "/reports": { title: "Reports", label: "Reports", roles: ["CLevel", "Admin", "Accountant"], render: () => renderListPattern("Reports", ["Report", "Date Range", "Owner", "Status"]) }
};

const navItems = [
  ["/dashboard", "Dashboard"],
  ["/catalog", "Catalog"],
  ["/inventory", "Inventory"],
  ["/crm", "CRM"],
  ["/operations", "Operations"],
  ["/payments", "Payments"],
  ["/reports", "Reports"]
];

document.getElementById("logout-button").addEventListener("click", logout);
window.addEventListener("hashchange", renderRoute);

checkHealth();
renderRoute();

function getAuth() {
  try {
    return JSON.parse(localStorage.getItem(authKey));
  } catch {
    return null;
  }
}

function setAuth(auth) {
  localStorage.setItem(authKey, JSON.stringify(auth));
}

function clearAuth() {
  localStorage.removeItem(authKey);
}

async function request(path, options = {}) {
  const auth = getAuth();
  const headers = new Headers(options.headers || {});
  headers.set("Content-Type", "application/json");

  if (auth?.accessToken) {
    headers.set("Authorization", `Bearer ${auth.accessToken}`);
  }

  let response = await fetch(`${apiBase}${path}`, { ...options, headers });

  if (response.status === 401 && auth?.refreshToken) {
    const refreshed = await refreshSession(auth.refreshToken);
    if (refreshed) {
      headers.set("Authorization", `Bearer ${refreshed.accessToken}`);
      response = await fetch(`${apiBase}${path}`, { ...options, headers });
    }
  }

  if (!response.ok) {
    const body = await response.text();
    throw new Error(body || response.statusText);
  }

  if (response.status === 204) {
    return null;
  }

  return response.json();
}

async function refreshSession(refreshToken) {
  try {
    const response = await fetch(`${apiBase}/api/v1/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken })
    });

    if (!response.ok) {
      clearAuth();
      return null;
    }

    const auth = await response.json();
    setAuth(auth);
    return auth;
  } catch {
    clearAuth();
    return null;
  }
}

async function checkHealth() {
  const pill = document.getElementById("health-pill");
  try {
    const healthBase = await resolveApiBase();
    const health = await fetch(`${healthBase}/health`).then((response) => response.json());
    pill.textContent = health.status === "Healthy" ? "API healthy" : "API degraded";
    pill.className = `status-pill ${health.status === "Healthy" ? "status-ok" : "status-warn"}`;
  } catch {
    pill.textContent = "API offline";
    pill.className = "status-pill status-warn";
  }
}

async function resolveApiBase(preferred = apiBase) {
  const candidates = [preferred, ...apiCandidates].filter((value, index, values) => value && values.indexOf(value) === index);

  for (const candidate of candidates) {
    try {
      const response = await fetch(`${candidate.replace(/\/$/, "")}/health`);
      if (response.ok) {
        apiBase = candidate.replace(/\/$/, "");
        localStorage.setItem("lensee.apiBase", apiBase);
        return apiBase;
      }
    } catch {
      // Try the next local development URL.
    }
  }

  return preferred.replace(/\/$/, "");
}

function currentPath() {
  return location.hash.replace("#", "") || "/dashboard";
}

function renderRoute() {
  const auth = getAuth();
  let path = currentPath();
  const route = routes[path] || routes["/dashboard"];
  document.body.classList.toggle("auth-page", path === "/login" && !auth);

  if (path !== "/login" && !auth) {
    location.hash = "/login";
    return;
  }

  if (auth && path === "/login") {
    location.hash = "/dashboard";
    return;
  }

  if (route.roles.length > 0 && auth && !route.roles.includes(auth.user.role)) {
    renderForbidden();
    return;
  }

  document.getElementById("page-title").textContent = route.title;
  document.getElementById("route-label").textContent = route.label;
  renderNav(auth);
  renderSession(auth);
  route.render();
}

function renderNav(auth) {
  const nav = document.getElementById("nav");
  const path = currentPath();
  nav.innerHTML = "";

  if (!auth) {
    nav.innerHTML = `<a href="#/login" aria-current="page">Sign in</a>`;
    return;
  }

  for (const [href, label] of navItems) {
    if (!routes[href].roles.includes(auth.user.role)) {
      continue;
    }

    const link = document.createElement("a");
    link.href = `#${href}`;
    link.textContent = label;
    if (path === href) {
      link.setAttribute("aria-current", "page");
    }
    nav.appendChild(link);
  }
}

function renderSession(auth) {
  const session = document.getElementById("session");
  session.textContent = auth ? `${roleLabel(auth.user.role)}${auth.user.locationId ? ` - ${auth.user.locationId}` : ""}` : "Not signed in";
  document.getElementById("logout-button").hidden = !auth;
}

function notice(message) {
  const area = document.getElementById("notification-area");
  area.innerHTML = `<div class="notice">${escapeHtml(message)}</div>`;
  setTimeout(() => {
    area.innerHTML = "";
  }, 4200);
}

function renderLogin() {
  const view = document.getElementById("view");
  document.getElementById("notification-area").innerHTML = "";
  view.innerHTML = `
    <section class="auth-layout">
      <div class="auth-copy">
        <span class="brand-mark auth-mark">L</span>
        <h2>Sign in to Lensee</h2>
        <p>Use a seeded development account to validate permissions across warehouse, accounting, executive, and admin workflows.</p>
        <div class="auth-status">
          <span id="login-health-dot" class="health-dot"></span>
          <span id="login-health-text">Checking API</span>
        </div>
      </div>

      <form class="auth-panel" id="login-form">
        <div class="field">
          <label for="api-base">API base URL</label>
          <input class="input" id="api-base" name="apiBase" value="${escapeHtml(apiBase)}" autocomplete="off" spellcheck="false">
        </div>

        <div class="field">
          <label for="username">Username</label>
          <input class="input" id="username" name="username" autocomplete="username" required autofocus>
        </div>

        <div class="field">
          <label for="password">Password</label>
          <div class="password-field">
            <input class="input" id="password" name="password" type="password" autocomplete="current-password" required>
            <button class="icon-button inline-icon" id="toggle-password" type="button" aria-label="Show password" title="Show password">S</button>
          </div>
        </div>

        <div class="login-error" id="login-error" role="alert" hidden></div>

        <button class="button auth-submit" id="login-submit" type="submit">Sign in</button>

        <div class="seed-grid" aria-label="Seeded development accounts">
          ${seededCredentials.map((credential) => `
            <button class="seed-button" type="button" data-username="${escapeHtml(credential.username)}" data-password="${escapeHtml(credential.password)}">
              <strong>${escapeHtml(credential.username)}</strong>
              <span>${escapeHtml(credential.role)}</span>
            </button>
          `).join("")}
        </div>
      </form>
    </section>
  `;

  checkLoginHealth();

  document.querySelectorAll(".seed-button").forEach((button) => {
    button.addEventListener("click", () => {
      document.getElementById("username").value = button.dataset.username;
      document.getElementById("password").value = button.dataset.password;
      document.getElementById("login-error").hidden = true;
    });
  });

  document.getElementById("toggle-password").addEventListener("click", () => {
    const password = document.getElementById("password");
    const isHidden = password.type === "password";
    password.type = isHidden ? "text" : "password";
    document.getElementById("toggle-password").textContent = isHidden ? "H" : "S";
  });

  document.getElementById("login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const submit = document.getElementById("login-submit");
    const error = document.getElementById("login-error");
    const form = new FormData(event.currentTarget);
    const nextApiBase = String(form.get("apiBase") || "").trim().replace(/\/$/, "");

    if (!nextApiBase) {
      error.textContent = "Enter the API base URL.";
      error.hidden = false;
      return;
    }

    localStorage.setItem("lensee.apiBase", nextApiBase);
    error.hidden = true;
    submit.disabled = true;
    submit.textContent = "Signing in";

    try {
      const auth = await loginRequest(nextApiBase, {
        method: "POST",
        body: JSON.stringify({
          username: form.get("username"),
          password: form.get("password")
        })
      });
      setAuth(auth);
      location.hash = "/dashboard";
      location.reload();
    } catch (exception) {
      error.textContent = getFriendlyLoginError(exception);
      error.hidden = false;
      submit.disabled = false;
      submit.textContent = "Sign in";
    }
  });
}

async function loginRequest(baseUrl, options) {
  const response = await fetch(`${baseUrl}/api/v1/auth/login`, {
    ...options,
    headers: { "Content-Type": "application/json" }
  });

  if (!response.ok) {
    const body = await response.text();
    throw new Error(body || response.statusText);
  }

  return response.json();
}

async function checkLoginHealth() {
  const dot = document.getElementById("login-health-dot");
  const text = document.getElementById("login-health-text");
  const baseInput = document.getElementById("api-base");

  try {
    const healthBase = await resolveApiBase(baseInput.value.replace(/\/$/, ""));
    baseInput.value = healthBase;
    const health = await fetch(`${healthBase}/health`).then((response) => response.json());
    dot.className = `health-dot ${health.status === "Healthy" ? "health-ok" : "health-warn"}`;
    text.textContent = health.status === "Healthy" ? "API healthy" : "API degraded";
  } catch {
    dot.className = "health-dot health-warn";
    text.textContent = "API offline";
  }
}

function getFriendlyLoginError(exception) {
  const message = exception instanceof Error ? exception.message : "";

  if (message.includes("Failed to fetch")) {
    return "Cannot reach the API. Check the API base URL and whether the host is running.";
  }

  if (message.includes("401") || message.includes("Unauthorized")) {
    return "Username or password is incorrect.";
  }

  if (message.includes("28P01")) {
    return "The API cannot connect to PostgreSQL. Reset or restart the dev database.";
  }

  return "Sign in failed. Check the account and API base URL.";
}

function renderDashboard() {
  document.getElementById("view").innerHTML = `
    <section class="grid">
      <div class="metric"><span>Open operations</span><strong>0</strong></div>
      <div class="metric"><span>Low stock alerts</span><strong>0</strong></div>
      <div class="metric"><span>Pending payments</span><strong>0</strong></div>
    </section>
    <section class="band">
      <h2>Today</h2>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Queue</th><th>Owner</th><th>Status</th><th>Next action</th></tr></thead>
          <tbody>
            <tr><td>Catalog</td><td>Admin</td><td>Ready</td><td>Add products in Sprint 3</td></tr>
            <tr><td>Inventory</td><td>Warehouse</td><td>Ready</td><td>Ledger starts Sprint 4</td></tr>
            <tr><td>Payments</td><td>Accounting</td><td>Ready</td><td>Workflow starts Sprint 8</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  `;
}

function renderInventory() {
  document.getElementById("view").innerHTML = `
    <section class="band">
      <h2>Location Stock Pattern</h2>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Location</th><th>SKU</th><th>Available</th><th>Reserved</th><th>Target</th></tr></thead>
          <tbody><tr><td>Main Warehouse</td><td>Pending catalog</td><td>0</td><td>0</td><td>0</td></tr></tbody>
        </table>
      </div>
    </section>
  `;
}

function renderOperations() {
  document.getElementById("view").innerHTML = `
    <section class="band">
      <h2>Operation Entry Pattern</h2>
      <form class="form">
        <div class="field">
          <label for="operation-type">Operation type</label>
          <select id="operation-type" class="select">
            <option>Wholesale sale</option>
            <option>Retail sale</option>
            <option>Reserve</option>
            <option>Change</option>
            <option>Return</option>
            <option>Inventory receipt</option>
          </select>
        </div>
        <div class="field">
          <label for="operation-notes">Notes</label>
          <input id="operation-notes" class="input">
        </div>
        <button class="button secondary" type="button">Save draft</button>
      </form>
    </section>
    <div class="mobile-action-bar">
      <button class="button" type="button">Scan</button>
      <button class="button secondary" type="button">Hold</button>
    </div>
  `;
}

function renderListPattern(title, headers) {
  const head = headers.map((header) => `<th>${escapeHtml(header)}</th>`).join("");
  const cols = headers.map(() => "<td>Pending API</td>").join("");
  document.getElementById("view").innerHTML = `
    <section class="band">
      <h2>${escapeHtml(title)}</h2>
      <div class="table-wrap">
        <table>
          <thead><tr>${head}</tr></thead>
          <tbody><tr>${cols}</tr></tbody>
        </table>
      </div>
    </section>
  `;
}

function renderForbidden() {
  document.getElementById("page-title").textContent = "Forbidden";
  document.getElementById("route-label").textContent = "Authorization";
  document.getElementById("view").innerHTML = `
    <section class="band">
      <h2>Access denied</h2>
      <p>This session cannot open that workspace.</p>
    </section>
  `;
}

async function logout() {
  const auth = getAuth();
  try {
    if (auth?.refreshToken) {
      await request("/api/v1/auth/logout", {
        method: "POST",
        body: JSON.stringify({ refreshToken: auth.refreshToken })
      });
    }
  } finally {
    clearAuth();
    location.hash = "/login";
  }
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#039;"
  })[character]);
}

function roleLabel(role) {
  return {
    CLevel: "C-Level",
    WarehouseClerk: "Warehouse Clerk"
  }[role] || role;
}
