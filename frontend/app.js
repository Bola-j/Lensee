let apiBase = localStorage.getItem("lensee.apiBase") || "http://localhost:5275";
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

let catalogCategories = [];
let categoryTree = [];
let catalogBrands = [];
let selectedProductId = null;
let activeRefreshTimer = null;

const routes = {
  "/login": { title: "Sign In", label: "Identity", roles: [], render: renderLogin },
  "/dashboard": { title: "Catalog Console", label: "Dashboard", roles: ["CLevel", "Admin", "Accountant", "WarehouseClerk"], render: renderDashboard },
  "/catalog": { title: "Catalog Workspace", label: "Catalog", roles: ["CLevel", "Admin", "WarehouseClerk"], render: renderCatalog }
};

const navItems = [
  ["/dashboard", "Dashboard"],
  ["/catalog", "Catalog"]
];

document.getElementById("logout-button").addEventListener("click", logout);
window.addEventListener("hashchange", renderRoute);
window.addEventListener("focus", () => {
  checkHealth();
  refreshActiveView();
});
window.addEventListener("lensee:data-mutated", () => {
  refreshActiveView();
});
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
  if (options.body !== undefined) {
    headers.set("Content-Type", "application/json");
  }
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
    const error = new Error(body || response.statusText);
    error.status = response.status;
    throw error;
  }

  const payload = response.status === 204 ? null : await response.json();
  const method = (options.method || "GET").toUpperCase();
  if (method !== "GET") {
    window.dispatchEvent(new CustomEvent("lensee:data-mutated", { detail: { path, method } }));
  }

  return payload;
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
      const normalized = candidate.replace(/\/$/, "");
      const response = await fetch(`${normalized}/health`);
      if (response.ok) {
        apiBase = normalized;
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
  const path = currentPath();
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
  scheduleRouteRefresh(path);
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

function notice(message, tone = "info") {
  const area = document.getElementById("notification-area");
  area.innerHTML = `<div class="notice notice-${tone}">${escapeHtml(message)}</div>`;
  setTimeout(() => {
    area.innerHTML = "";
  }, 5200);
}

function scheduleRouteRefresh(path) {
  window.clearInterval(activeRefreshTimer);
  activeRefreshTimer = null;
  if (path === "/login") {
    return;
  }

  activeRefreshTimer = window.setInterval(() => {
    refreshActiveView();
    updateNotificationBadge();
  }, 15000);
}

async function refreshActiveView() {
  if (!getAuth()) {
    return;
  }

  try {
    switch (currentPath()) {
      case "/catalog":
        await loadProducts();
        break;
    }
  } catch {
    // Individual loaders already render actionable errors.
  }
}

function renderLogin() {
  document.getElementById("notification-area").innerHTML = "";
  document.getElementById("view").innerHTML = `
    <section class="auth-layout">
      <div class="auth-copy">
        <span class="brand-mark auth-mark">L</span>
        <h2>Sign in to Lensee</h2>
        <p>Use a seeded development account to validate permissions across warehouse, accounting, executive, and admin workflows.</p>
        <div class="auth-status"><span id="login-health-dot" class="health-dot"></span><span id="login-health-text">Checking API</span></div>
      </div>
      <form class="auth-panel" id="login-form">
        <div class="field"><label for="api-base">API base URL</label><input class="input" id="api-base" name="apiBase" value="${escapeHtml(apiBase)}" autocomplete="off" spellcheck="false"></div>
        <div class="field"><label for="username">Username</label><input class="input" id="username" name="username" autocomplete="username" required autofocus></div>
        <div class="field"><label for="password">Password</label><div class="password-field"><input class="input" id="password" name="password" type="password" autocomplete="current-password" required><button class="icon-button inline-icon" id="toggle-password" type="button" aria-label="Show password" title="Show password">S</button></div></div>
        <div class="login-error" id="login-error" role="alert" hidden></div>
        <button class="button auth-submit" id="login-submit" type="submit">Sign in</button>
        <div class="dev-tools"><button class="button secondary" id="check-api-button" type="button">Check API</button></div>
        <div class="seed-grid" aria-label="Seeded development accounts">
          ${seededCredentials.map((credential) => `<button class="seed-button" type="button" data-username="${escapeHtml(credential.username)}" data-password="${escapeHtml(credential.password)}"><strong>${escapeHtml(credential.username)}</strong><span>${escapeHtml(credential.role)}</span></button>`).join("")}
        </div>
      </form>
    </section>`;

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
  document.getElementById("check-api-button").addEventListener("click", checkLoginHealth);
  document.getElementById("login-form").addEventListener("submit", login);
}

async function login(event) {
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
  apiBase = nextApiBase;
  error.hidden = true;
  submit.disabled = true;
  submit.textContent = "Signing in";
  try {
    const auth = await loginRequest(nextApiBase, {
      method: "POST",
      body: JSON.stringify({ username: form.get("username"), password: form.get("password") })
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
}

async function loginRequest(baseUrl, options) {
  const response = await fetch(`${baseUrl}/api/v1/auth/login`, { ...options, headers: { "Content-Type": "application/json" } });
  if (!response.ok) {
    throw new Error(await response.text() || response.statusText);
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

function renderDashboard() {
  document.getElementById("view").innerHTML = `
    <section class="grid">
      <div class="metric"><span>Current scope</span><strong>Catalog</strong></div>
      <div class="metric"><span>Writable role</span><strong>Admin</strong></div>
      <div class="metric"><span>Read-only roles</span><strong>C-Level / Clerk</strong></div>
    </section>
    <section class="band"><h2>Stable scope</h2><div class="table-wrap"><table><thead><tr><th>Area</th><th>Owner</th><th>Status</th><th>Allowed action</th></tr></thead><tbody>
      <tr><td>Categories</td><td>Admin</td><td>Ready</td><td>Create and edit</td></tr>
      <tr><td>Brands</td><td>Admin</td><td>Ready</td><td>Create and edit</td></tr>
      <tr><td>Products and SKUs</td><td>Admin</td><td>Ready</td><td>Create, edit, deactivate, reactivate</td></tr>
    </tbody></table></div></section>`;
}

function renderCatalog() {
  const auth = getAuth();
  const canWrite = auth?.user.role === "Admin";
  document.getElementById("view").innerHTML = `
    <section class="catalog-hero">
      <div>
        <p class="eyebrow">Catalog</p>
        <h2>Catalog validation workspace</h2>
        <p>Exercise products, SKUs, reference data, active states, and role permissions against the API.</p>
      </div>
      <div class="scenario-grid">
        ${scenarioCard("Health", "API reachable", "health-pill")}
        ${scenarioCard("Role", canWrite ? "Admin write access" : "Read-only access", canWrite ? "status-ok" : "status-muted")}
        ${scenarioCard("Catalog", "Products/SKUs active", "status-muted")}
      </div>
    </section>

    <section class="catalog-layout">
      <aside class="catalog-side">
        <section class="band compact-band">
          <div class="section-head"><h2>Filters</h2><button id="catalog-refresh" class="button secondary" type="button">Refresh</button></div>
          <div class="field"><label for="catalog-search">Search</label><input id="catalog-search" class="input" placeholder="Product, brand, category"></div>
          <label class="check-field"><input id="catalog-include-inactive" type="checkbox" checked><span>Show inactive products</span></label>
          <div class="muted-text" id="catalog-count">Loading</div>
        </section>
        <section class="band compact-band">
          <h2>Scenarios</h2>
          <ol class="scenario-list">
            <li>Admin: create category and brand.</li>
            <li>Admin: create lens product with clinical JSON.</li>
            <li>Admin: create SKU, then deactivate/reactivate it.</li>
            <li>C-Level or clerk: verify read-only catalog.</li>
            <li>Accountant: verify catalog is unavailable.</li>
          </ol>
        </section>
      </aside>

      <section class="catalog-main">
        <section class="band">
          <div class="section-head"><h2>Products</h2><span class="status-pill ${canWrite ? "status-ok" : "status-muted"}">${canWrite ? "Writable" : "Read only"}</span></div>
          <div class="table-wrap"><table><thead><tr><th>Name</th><th>Type</th><th>Brand</th><th>Category</th><th>Pack</th><th>Status</th>${canWrite ? "<th>Actions</th>" : ""}</tr></thead><tbody id="catalog-products"><tr><td colspan="${canWrite ? 7 : 6}">Loading catalog</td></tr></tbody></table></div>
        </section>
        <section class="catalog-detail-grid">
          <section class="band" id="catalog-detail"><h2>Product detail</h2><p class="muted-text">Select a product to inspect SKUs and status.</p></section>
          ${canWrite ? renderCatalogWritePanel() : `<section class="band"><h2>Write controls</h2><p class="muted-text">This role can inspect catalog data but cannot change it.</p></section>`}
        </section>
      </section>
    </section>`;

  document.getElementById("catalog-refresh").addEventListener("click", refreshCatalogWorkspace);
  document.getElementById("catalog-search").addEventListener("input", debounce(loadCatalogProducts, 250));
  document.getElementById("catalog-include-inactive").addEventListener("change", loadCatalogProducts);

  if (canWrite) {
    wireCatalogWritePanel();
  }
  refreshCatalogWorkspace();
}

function scenarioCard(title, value, tone) {
  return `<div class="scenario-card"><span>${escapeHtml(title)}</span><strong class="${escapeHtml(tone)}">${escapeHtml(value)}</strong></div>`;
}

function renderCatalogWritePanel() {
  return `
    <section class="write-stack">
      <section class="band">
        <div class="section-head"><h2>Product editor</h2><button class="button secondary" id="product-reset" type="button">New</button></div>
        <form class="form wide-form" id="product-form">
          <input type="hidden" id="product-id">
          <div class="form-error" id="product-error" hidden></div>
          <div class="form-grid">
            <div class="field"><label for="product-name">Name</label><input id="product-name" class="input" required></div>
            <div class="field"><label for="product-type">Type</label><select id="product-type" class="select"><option value="Lens">Lens</option><option value="Solution">Solution</option></select></div>
            <div class="field"><label for="product-category">Category</label><select id="product-category" class="select" required></select></div>
            <div class="field"><label for="product-brand">Brand</label><select id="product-brand" class="select" required></select></div>
            <div class="field"><label for="product-sell-mode">Sell mode</label><select id="product-sell-mode" class="select"><option value="SinglePiece">Single piece</option><option value="SealedPackOnly">Sealed pack only</option><option value="Both">Both</option></select></div>
            <div class="field"><label for="product-pieces">Pieces per pack</label><input id="product-pieces" class="input" type="number" min="1" value="1"></div>
            <div class="field"><label for="product-expiry">Expiry source</label><select id="product-expiry" class="select"><option value="Batch">Batch expiry date</option><option value="None">No batch expiry</option></select></div>
            <div class="field"><label for="product-opened-expiry-duration">Valid after opening</label><input id="product-opened-expiry-duration" class="input" type="number" min="1" step="1" value="6"></div>
            <div class="field"><label for="product-opened-expiry-unit">Duration unit</label><select id="product-opened-expiry-unit" class="select"><option value="Daily">Days</option><option value="Monthly" selected>Months</option><option value="Annually">Years</option></select></div>
          </div>
          <div class="form-grid two">
            <div class="field"><label for="product-clinical">Clinical params JSON</label><textarea id="product-clinical" class="input textarea">{"duration":"monthly"}</textarea></div>
            <div class="field"><label for="product-attributes">Extended attributes JSON</label><textarea id="product-attributes" class="input textarea">{}</textarea></div>
          </div>
            <div class="form-actions"><button class="button" id="product-submit" type="submit">Create product</button><span class="muted-text" id="product-mode">New product</span></div>
        </form>
      </section>

      <section class="catalog-admin-grid">
        <section class="band compact-band">
          <div class="section-head"><h2>Categories</h2><button class="button secondary" id="category-reset" type="button">New</button></div>
          <form class="form" id="category-form">
            <input type="hidden" id="category-id">
            <div class="form-error" id="category-error" hidden></div>
            <div class="field"><label for="category-name">Name</label><input id="category-name" class="input" required></div>
            <div class="field"><label for="category-parent">Parent</label><select id="category-parent" class="select"><option value="">None</option></select></div>
            <div class="form-actions"><button class="button" id="category-submit" type="submit">Create category</button><span class="muted-text" id="category-mode">New category</span></div>
          </form>
          <div class="tree-list" id="category-list"></div>
        </section>
        <section class="band compact-band">
          <div class="section-head"><h2>Brands</h2><button class="button secondary" id="brand-reset" type="button">New</button></div>
          <form class="form" id="brand-form">
            <input type="hidden" id="brand-id">
            <div class="form-error" id="brand-error" hidden></div>
            <div class="field"><label for="brand-name">Name</label><input id="brand-name" class="input" required></div>
            <div class="form-actions"><button class="button" id="brand-submit" type="submit">Create brand</button><span class="muted-text" id="brand-mode">New brand</span></div>
          </form>
          <div class="chip-list" id="brand-list"></div>
        </section>
      </section>
    </section>`;
}

function wireCatalogWritePanel() {
  document.getElementById("product-type").addEventListener("change", syncProductTypeFields);
  document.getElementById("product-reset").addEventListener("click", resetProductForm);
  document.getElementById("category-reset").addEventListener("click", resetCategoryForm);
  document.getElementById("brand-reset").addEventListener("click", resetBrandForm);
  document.getElementById("category-form").addEventListener("submit", saveCategory);
  document.getElementById("brand-form").addEventListener("submit", saveBrand);
  document.getElementById("product-form").addEventListener("submit", saveProduct);
  syncProductTypeFields();
}

async function refreshCatalogWorkspace() {
  await loadCatalogLookups();
  await loadCatalogProducts();
  if (selectedProductId) {
    await loadCatalogDetail(selectedProductId);
  }
}

async function loadCatalogLookups() {
  try {
    const [categories, tree, brands] = await Promise.all([
      request("/api/v1/catalog/categories"),
      request("/api/v1/catalog/categories/tree"),
      request("/api/v1/catalog/brands")
    ]);
    catalogCategories = categories;
    categoryTree = tree;
    catalogBrands = brands;
    refreshLookupControls();
  } catch (exception) {
    notice(getFriendlyApiError(exception), "error");
  }
}

function refreshLookupControls() {
  const canWrite = getAuth()?.user.role === "Admin";
  if (!canWrite) {
    return;
  }
  fillCategorySelect(document.getElementById("category-parent"), true);
  fillCategorySelect(document.getElementById("product-category"), false);
  fillSelect(document.getElementById("product-brand"), catalogBrands);
  renderCatalogReferenceLists();
}

function renderCatalogReferenceLists() {
  const categoryList = document.getElementById("category-list");
  const brandList = document.getElementById("brand-list");
  if (categoryList) {
    categoryList.innerHTML = renderCategoryTree(categoryTree);
    categoryList.querySelectorAll("[data-category-id]").forEach((button) => {
      button.addEventListener("click", () => {
        const category = catalogCategories.find((value) => value.id === button.dataset.categoryId);
        if (category) {
          document.getElementById("category-id").value = category.id;
          document.getElementById("category-name").value = category.name;
          document.getElementById("category-parent").value = category.parentId || "";
          document.getElementById("category-submit").textContent = "Update category";
          document.getElementById("category-mode").textContent = `Editing ${category.name}`;
          clearFormError("category-error");
          document.getElementById("category-name").focus();
        }
      });
    });
  }
  if (brandList) {
    brandList.innerHTML = catalogBrands.map((brand) => `<button class="chip" type="button" data-brand-id="${escapeHtml(brand.id)}">Edit ${escapeHtml(brand.name)}</button>`).join("");
    brandList.querySelectorAll("[data-brand-id]").forEach((button) => {
      button.addEventListener("click", () => {
        const brand = catalogBrands.find((value) => value.id === button.dataset.brandId);
        if (brand) {
          document.getElementById("brand-id").value = brand.id;
          document.getElementById("brand-name").value = brand.name;
          document.getElementById("brand-submit").textContent = "Update brand";
          document.getElementById("brand-mode").textContent = `Editing ${brand.name}`;
          clearFormError("brand-error");
          document.getElementById("brand-name").focus();
        }
      });
    });
  }
}

function renderCategoryTree(nodes, depth = 0) {
  if (nodes.length === 0) {
    return depth === 0 ? `<p class="muted-text">No categories</p>` : "";
  }
  return nodes.map((node) => `
    <div class="tree-row" style="--depth:${depth}">
      <button class="chip" type="button" data-category-id="${escapeHtml(node.id)}">Edit ${escapeHtml(node.name)}</button>
    </div>
    ${renderCategoryTree(node.children || [], depth + 1)}
  `).join("");
}

function fillCategorySelect(select, includeEmpty) {
  const current = select.value;
  const options = [];
  flattenCategoryOptions(categoryTree, options);
  select.innerHTML = includeEmpty ? `<option value="">None</option>` : "";
  select.innerHTML += options.map((item) => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.label)}</option>`).join("");
  if ([...select.options].some((option) => option.value === current)) {
    select.value = current;
  }
}

function flattenCategoryOptions(nodes, output, depth = 0) {
  for (const node of nodes) {
    output.push({ id: node.id, label: `${"  ".repeat(depth)}${node.name}` });
    flattenCategoryOptions(node.children || [], output, depth + 1);
  }
}

function fillSelect(select, items) {
  const current = select.value;
  select.innerHTML = items.map((item) => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join("");
  if ([...select.options].some((option) => option.value === current)) {
    select.value = current;
  }
}

async function loadCatalogProducts() {
  const tbody = document.getElementById("catalog-products");
  const count = document.getElementById("catalog-count");
  const canWrite = getAuth()?.user.role === "Admin";
  const search = document.getElementById("catalog-search").value.trim();
  const includeInactive = document.getElementById("catalog-include-inactive").checked;
  const params = new URLSearchParams({ page: "1", pageSize: "50", includeInactive: String(includeInactive) });
  if (search) {
    params.set("search", search);
  }

  tbody.innerHTML = `<tr><td colspan="${canWrite ? 7 : 6}">Loading catalog</td></tr>`;
  try {
    const result = await request(`/api/v1/catalog/products?${params}`);
    count.textContent = `${result.totalCount} product${result.totalCount === 1 ? "" : "s"}`;
    tbody.innerHTML = result.items.length === 0
      ? `<tr><td colspan="${canWrite ? 7 : 6}">No products found</td></tr>`
      : result.items.map((product) => `
        <tr class="click-row ${product.id === selectedProductId ? "selected-row" : ""}" data-product-id="${escapeHtml(product.id)}">
          <td>${escapeHtml(product.name)}</td><td>${escapeHtml(product.productType)}</td><td>${escapeHtml(product.brandName)}</td>
          <td>${escapeHtml(product.categoryName)}</td><td>${formatPackHint(product)}</td>
          <td><span class="status-pill ${product.isActive ? "status-ok" : "status-muted"}">${product.isActive ? "Active" : "Inactive"}</span></td>
          ${canWrite ? `<td><button class="button secondary table-action" type="button" data-product-edit="${escapeHtml(product.id)}">Edit</button></td>` : ""}
        </tr>`).join("");
    tbody.querySelectorAll("[data-product-id]").forEach((row) => row.addEventListener("click", () => loadCatalogDetail(row.dataset.productId)));
    tbody.querySelectorAll("[data-product-edit]").forEach((button) => button.addEventListener("click", (event) => {
      event.stopPropagation();
      editProductFromList(button.dataset.productEdit);
    }));
  } catch (exception) {
    tbody.innerHTML = `<tr><td colspan="${canWrite ? 7 : 6}">${escapeHtml(getFriendlyApiError(exception))}</td></tr>`;
    count.textContent = "";
  }
}

async function editProductFromList(productId) {
  try {
    const product = await request(`/api/v1/catalog/products/${productId}`);
    selectedProductId = productId;
    fillProductForm(product);
    await loadCatalogDetail(productId);
  } catch (exception) {
    notice(getFriendlyApiError(exception), "error");
  }
}

async function loadCatalogDetail(productId) {
  selectedProductId = productId;
  const detail = document.getElementById("catalog-detail");
  const canWrite = getAuth()?.user.role === "Admin";
  detail.innerHTML = `<h2>Product detail</h2><p>Loading product</p>`;
  try {
    const product = await request(`/api/v1/catalog/products/${productId}`);
    detail.innerHTML = `
      <div class="section-head">
        <div><h2>${escapeHtml(product.name)}</h2><p class="muted-text">${escapeHtml(product.brandName)} - ${escapeHtml(product.categoryName)}</p></div>
        <div class="inline-actions">
          <span class="status-pill ${product.isActive ? "status-ok" : "status-muted"}">${product.isActive ? "Active" : "Inactive"}</span>
          ${canWrite ? `<button class="button secondary" id="edit-product" type="button">Edit</button><button class="button secondary" id="toggle-product" type="button">${product.isActive ? "Deactivate" : "Reactivate"}</button>` : ""}
        </div>
      </div>
      <div class="detail-grid">
        <div><span>Type</span><strong>${escapeHtml(product.productType)}</strong></div>
        <div><span>Sell mode</span><strong>${escapeHtml(product.sellMode || "Not set")}</strong></div>
        <div><span>Pieces per pack</span><strong>${escapeHtml(product.piecesPerPack || "Not set")}</strong></div>
        <div><span>Expiry</span><strong>${escapeHtml(product.expiryType || "Not set")}</strong></div>
        <div><span>Valid after opening</span><strong>${escapeHtml(product.openedExpiryDuration || "Not set")}</strong></div>
      </div>
      <p class="muted-text">Actual opened expiry is calculated later as the earlier of opened date plus product opening validity, or the batch expiry date.</p>
      ${renderSkuSection(product, canWrite)}`;
    if (canWrite) {
      wireProductAdminActions(product);
    }
    await loadCatalogProducts();
  } catch (exception) {
    detail.innerHTML = `<h2>Product detail</h2><p>${escapeHtml(getFriendlyApiError(exception))}</p>`;
  }
}

function renderSkuSection(product, canWrite) {
  return `
    <h3>SKUs</h3>
    ${canWrite ? `
      <form class="form wide-form compact-form" id="sku-form">
        <input type="hidden" id="sku-id"><div class="form-error" id="sku-error" hidden></div>
        <div class="form-grid">
          <div class="sku-preview"><span>Generated SKU</span><strong id="sku-code-preview">Derived after save</strong></div>
          <div class="field"><label for="sku-power-sign">Power sign</label><select id="sku-power-sign" class="select"><option value="">None</option><option value="+">+</option><option value="-">-</option></select></div>
          <div class="field"><label for="sku-power-value">Power value</label><input id="sku-power-value" class="input" type="number" step="0.25" min="0"></div>
          <div class="field"><label for="sku-color">Color</label><input id="sku-color" class="input"></div>
          <div class="field"><label for="sku-size">Size</label><input id="sku-size" class="input"></div>
          <div class="field"><label for="sku-barcode">Barcode</label><input id="sku-barcode" class="input"></div>
        </div>
        <div class="form-actions"><button class="button" type="submit">Save SKU</button><button class="button secondary" id="sku-reset" type="button">Clear</button></div>
      </form>` : ""}
    <div class="table-wrap"><table><thead><tr><th>SKU</th><th>Power</th><th>Color</th><th>Size</th><th>Barcode</th><th>Status</th>${canWrite ? "<th>Actions</th>" : ""}</tr></thead><tbody>
      ${product.skus.length === 0 ? `<tr><td colspan="${canWrite ? 7 : 6}">No SKUs</td></tr>` : product.skus.map((sku) => `
        <tr><td>${escapeHtml(sku.skuCode)}</td><td>${escapeHtml(formatPower(sku))}</td><td>${escapeHtml(sku.colorName || "-")}</td><td>${escapeHtml(sku.size || "-")}</td><td>${escapeHtml(sku.barcode || "-")}</td>
        <td><span class="status-pill ${sku.isActive ? "status-ok" : "status-muted"}">${sku.isActive ? "Active" : "Inactive"}</span></td>
        ${canWrite ? `<td><button class="button secondary table-action" type="button" data-edit-sku="${escapeHtml(sku.id)}">Edit</button><button class="button secondary table-action" type="button" data-toggle-sku="${escapeHtml(sku.id)}">${sku.isActive ? "Deactivate" : "Reactivate"}</button></td>` : ""}</tr>`).join("")}
    </tbody></table></div>`;
}

function wireProductAdminActions(product) {
  document.getElementById("edit-product").addEventListener("click", () => fillProductForm(product));
  document.getElementById("toggle-product").addEventListener("click", async () => {
    if (await saveCatalogEntity(`/api/v1/catalog/products/${product.id}/${product.isActive ? "deactivate" : "reactivate"}`, "PATCH", null, "Product status updated.")) {
      await loadCatalogDetail(product.id);
    }
  });
  document.getElementById("sku-reset").addEventListener("click", resetSkuForm);
  ["sku-power-sign", "sku-power-value", "sku-color", "sku-size"].forEach((id) => {
    document.getElementById(id).addEventListener("input", () => updateSkuPreview(product));
    document.getElementById(id).addEventListener("change", () => updateSkuPreview(product));
  });
  updateSkuPreview(product);
  document.getElementById("sku-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const validation = validateSkuForm(product.productType);
    if (validation) {
      showFormError("sku-error", validation);
      return;
    }
    const skuId = document.getElementById("sku-id").value;
    const saved = await saveCatalogEntity(skuId ? `/api/v1/catalog/skus/${skuId}` : `/api/v1/catalog/products/${product.id}/skus`, skuId ? "PUT" : "POST", readSkuForm(), "SKU saved.", "sku-error");
    if (saved) {
      resetSkuForm();
      await loadCatalogDetail(product.id);
    }
  });
  document.querySelectorAll("[data-edit-sku]").forEach((button) => button.addEventListener("click", () => {
    const sku = product.skus.find((value) => value.id === button.dataset.editSku);
    if (sku) {
      fillSkuForm(sku);
    }
  }));
  document.querySelectorAll("[data-toggle-sku]").forEach((button) => button.addEventListener("click", async () => {
    const sku = product.skus.find((value) => value.id === button.dataset.toggleSku);
    if (sku && await saveCatalogEntity(`/api/v1/catalog/skus/${sku.id}/${sku.isActive ? "deactivate" : "reactivate"}`, "PATCH", null, "SKU status updated.")) {
      await loadCatalogDetail(product.id);
    }
  }));
}

async function saveCategory(event) {
  event.preventDefault();
  const id = document.getElementById("category-id").value;
  const saved = await saveCatalogEntity(id ? `/api/v1/catalog/categories/${id}` : "/api/v1/catalog/categories", id ? "PUT" : "POST", {
    name: document.getElementById("category-name").value,
    parentId: document.getElementById("category-parent").value || null
  }, "Category saved.", "category-error");
  if (saved) {
    resetCategoryForm();
    await loadCatalogLookups();
  }
}

async function saveBrand(event) {
  event.preventDefault();
  const id = document.getElementById("brand-id").value;
  const saved = await saveCatalogEntity(id ? `/api/v1/catalog/brands/${id}` : "/api/v1/catalog/brands", id ? "PUT" : "POST", {
    name: document.getElementById("brand-name").value
  }, "Brand saved.", "brand-error");
  if (saved) {
    resetBrandForm();
    await loadCatalogLookups();
  }
}

async function saveProduct(event) {
  event.preventDefault();
  const validation = validateProductForm();
  if (validation) {
    showFormError("product-error", validation);
    return;
  }
  const id = document.getElementById("product-id").value;
  const saved = await saveCatalogEntity(id ? `/api/v1/catalog/products/${id}` : "/api/v1/catalog/products", id ? "PUT" : "POST", readProductForm(), "Product saved.", "product-error");
  if (saved) {
    resetProductForm();
    await loadCatalogProducts();
    if (id) {
      await loadCatalogDetail(id);
    }
  }
}

async function saveCatalogEntity(path, method, payload, successMessage, errorId) {
  clearFormError(errorId);
  try {
    await request(path, { method, body: payload === null ? undefined : JSON.stringify(payload) });
    notice(successMessage, "success");
    return true;
  } catch (exception) {
    const message = getFriendlyCatalogWriteError(exception);
    if (errorId) {
      showFormError(errorId, message);
    }
    notice(message, "error");
    return false;
  }
}

function validateProductForm() {
  const name = document.getElementById("product-name").value.trim();
  const type = document.getElementById("product-type").value;
  const category = document.getElementById("product-category").value;
  const brand = document.getElementById("product-brand").value;
  const pieces = Number(document.getElementById("product-pieces").value || 0);
  const openedExpiry = Number(document.getElementById("product-opened-expiry-duration").value || 0);
  const openedExpiryUnit = document.getElementById("product-opened-expiry-unit").value;
  const clinical = document.getElementById("product-clinical").value.trim();
  const attributes = document.getElementById("product-attributes").value.trim();
  if (!name || !category || !brand) {
    return "Product name, category, and brand are required.";
  }
  if (pieces <= 0) {
    return "Pieces per pack must be greater than zero.";
  }
  if (!Number.isInteger(openedExpiry) || openedExpiry <= 0) {
    return "Valid after opening must be a positive whole number.";
  }
  if (!["Daily", "Monthly", "Annually"].includes(openedExpiryUnit)) {
    return "Valid after opening duration unit must be selected.";
  }
  if (type !== "Solution" && !clinical) {
    return "Clinical parameters JSON is required for lens products.";
  }
  return validateJson(clinical, "Clinical params") || validateJson(attributes, "Extended attributes");
}

function validateSkuForm(productType) {
  const color = document.getElementById("sku-color").value.trim();
  const size = document.getElementById("sku-size").value.trim();
  if (productType !== "Solution" && !color) {
    return "Color is required for lens SKUs.";
  }
  if (productType === "Solution" && !size) {
    return "Size is required for solution SKUs.";
  }
  return null;
}

function validateJson(value, label) {
  if (!value) {
    return null;
  }
  try {
    JSON.parse(value);
    return null;
  } catch {
    return `${label} must be valid JSON.`;
  }
}

function readProductForm() {
  const type = document.getElementById("product-type").value;
  const pieces = document.getElementById("product-pieces").value;
  const openedAmount = document.getElementById("product-opened-expiry-duration").value;
  const openedUnit = document.getElementById("product-opened-expiry-unit").value;
  return {
    categoryId: document.getElementById("product-category").value,
    brandId: document.getElementById("product-brand").value,
    name: document.getElementById("product-name").value,
    productType: type,
    expiryType: document.getElementById("product-expiry").value,
    sealedExpiryDuration: null,
    sealedExpiryRate: null,
    openedExpiryDuration: openedAmount ? buildDuration(openedAmount, openedUnit) : null,
    piecesPerPack: pieces ? Number(pieces) : null,
    sellMode: document.getElementById("product-sell-mode").value,
    clinicalParams: type === "Solution" ? null : document.getElementById("product-clinical").value || null,
    extendedAttributes: document.getElementById("product-attributes").value || null
  };
}

function parseDurationAmount(duration) {
  const match = String(duration || "").trim().match(/^([1-9][0-9]*)\s+(day|days|month|months|year|years)$/i);
  return match ? match[1] : "";
}

function parseDurationRate(duration) {
  const match = String(duration || "").trim().match(/^[1-9][0-9]*\s+(day|days|month|months|year|years)$/i);
  const unit = match ? match[1].toLowerCase() : "";
  if (unit.startsWith("day")) {
    return "Daily";
  }
  if (unit.startsWith("year")) {
    return "Annually";
  }
  return "Monthly";
}

function buildDuration(amount, rate) {
  const value = Number(amount);
  const unit = rate === "Daily"
    ? value === 1 ? "day" : "days"
    : rate === "Annually"
      ? value === 1 ? "year" : "years"
      : value === 1 ? "month" : "months";
  return `${value} ${unit}`;
}

function fillProductForm(product) {
  document.getElementById("product-id").value = product.id;
  document.getElementById("product-name").value = product.name;
  document.getElementById("product-type").value = product.productType;
  document.getElementById("product-category").value = product.categoryId;
  document.getElementById("product-brand").value = product.brandId;
  document.getElementById("product-sell-mode").value = product.sellMode || "SinglePiece";
  document.getElementById("product-pieces").value = product.piecesPerPack || "";
  document.getElementById("product-expiry").value = product.expiryType || "Batch";
  document.getElementById("product-opened-expiry-duration").value = parseDurationAmount(product.openedExpiryDuration) || "";
  document.getElementById("product-opened-expiry-unit").value = parseDurationRate(product.openedExpiryDuration);
  document.getElementById("product-clinical").value = product.clinicalParams || "";
  document.getElementById("product-attributes").value = product.extendedAttributes || "";
  document.getElementById("product-submit").textContent = "Update product";
  document.getElementById("product-mode").textContent = `Editing ${product.name}`;
  syncProductTypeFields();
  document.getElementById("product-name").focus();
}

function resetProductForm() {
  document.getElementById("product-id").value = "";
  document.getElementById("product-name").value = "";
  document.getElementById("product-type").value = "Lens";
  document.getElementById("product-sell-mode").value = "SinglePiece";
  document.getElementById("product-pieces").value = "1";
  document.getElementById("product-expiry").value = "Batch";
  document.getElementById("product-opened-expiry-duration").value = "6";
  document.getElementById("product-opened-expiry-unit").value = "Monthly";
  document.getElementById("product-clinical").value = '{"duration":"monthly"}';
  document.getElementById("product-attributes").value = "{}";
  document.getElementById("product-submit").textContent = "Create product";
  document.getElementById("product-mode").textContent = "New product";
  clearFormError("product-error");
  syncProductTypeFields();
}

function readSkuForm() {
  const powerValue = document.getElementById("sku-power-value").value;
  return {
    powerSign: document.getElementById("sku-power-sign").value || null,
    powerValue: powerValue ? Number(powerValue) : null,
    colorName: document.getElementById("sku-color").value || null,
    size: document.getElementById("sku-size").value || null,
    barcode: document.getElementById("sku-barcode").value || null
  };
}

function fillSkuForm(sku) {
  document.getElementById("sku-id").value = sku.id;
  document.getElementById("sku-code-preview").textContent = sku.skuCode;
  document.getElementById("sku-power-sign").value = sku.powerSign || "";
  document.getElementById("sku-power-value").value = sku.powerValue ?? "";
  document.getElementById("sku-color").value = sku.colorName || "";
  document.getElementById("sku-size").value = sku.size || "";
  document.getElementById("sku-barcode").value = sku.barcode || "";
  document.getElementById("sku-power-sign").focus();
}

function resetSkuForm() {
  ["sku-id", "sku-power-sign", "sku-power-value", "sku-color", "sku-size", "sku-barcode"].forEach((id) => {
    document.getElementById(id).value = "";
  });
  const preview = document.getElementById("sku-code-preview");
  if (preview) {
    preview.textContent = "Derived after save";
  }
  clearFormError("sku-error");
}

function updateSkuPreview(product) {
  const preview = document.getElementById("sku-code-preview");
  if (!preview) {
    return;
  }

  preview.textContent = generateSkuPreview(product, readSkuForm());
}

function generateSkuPreview(product, sku) {
  const brand = toCode(product.brandName, 3);
  const category = toCategoryCode(product.categoryName);
  if (product.productType === "Solution") {
    return joinSkuParts(brand, category, toCode(sku.size, 8));
  }

  return joinSkuParts(
    brand,
    category,
    formatSkuPower(sku.powerSign, sku.powerValue),
    toCode(sku.colorName, 8));
}

function toCategoryCode(value) {
  const parts = String(value || "")
    .split(/[ \/_-]+/)
    .filter((part) => part && !["and", "of"].includes(part.toLowerCase()));
  if (parts.length > 1) {
    return toCode(parts.map((part) => part[0]).join(""), 3);
  }

  return toCode(value, 3);
}

function toCode(value, maxLength) {
  const code = String(value || "NA")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/[^a-z0-9]/gi, "")
    .toUpperCase();

  return (code || "NA").slice(0, maxLength);
}

function formatSkuPower(sign, value) {
  if (value === null || value === undefined || value === "") {
    return "P0";
  }

  return `${sign === "-" ? "M" : "P"}${String(Number(value).toFixed(2)).replace(/\.?0+$/, "").replace(".", "")}`;
}

function joinSkuParts(...parts) {
  return parts.filter(Boolean).join("-");
}

function resetCategoryForm() {
  document.getElementById("category-id").value = "";
  document.getElementById("category-name").value = "";
  document.getElementById("category-parent").value = "";
  document.getElementById("category-submit").textContent = "Create category";
  document.getElementById("category-mode").textContent = "New category";
  clearFormError("category-error");
}

function resetBrandForm() {
  document.getElementById("brand-id").value = "";
  document.getElementById("brand-name").value = "";
  document.getElementById("brand-submit").textContent = "Create brand";
  document.getElementById("brand-mode").textContent = "New brand";
  clearFormError("brand-error");
}

function syncProductTypeFields() {
  const type = document.getElementById("product-type")?.value;
  const clinical = document.getElementById("product-clinical");
  if (!clinical) {
    return;
  }
  clinical.disabled = type === "Solution";
  if (type === "Solution") {
    clinical.value = "";
  } else if (!clinical.value.trim()) {
    clinical.value = '{"duration":"monthly"}';
  }
}

function showFormError(id, message) {
  const element = document.getElementById(id);
  if (!element) {
    return;
  }
  element.textContent = message;
  element.hidden = false;
}

function clearFormError(id) {
  if (!id) {
    return;
  }
  const element = document.getElementById(id);
  if (element) {
    element.hidden = true;
    element.textContent = "";
  }
}

function renderInventory() {
  const auth = getAuth();
  const canWrite = auth?.user.role === "Admin";
  document.getElementById("view").innerHTML = `
    <section class="catalog-hero">
      <div>
        <p class="eyebrow">Sprint 4</p>
        <h2>Inventory stock workspace</h2>
        <p>Review locations, stock balances, batch expiry dates, and append-only stock transaction history.</p>
      </div>
      <div class="scenario-grid">
        ${scenarioCard("Role", canWrite ? "Admin target editing" : "Read-only inventory", canWrite ? "status-ok" : "status-muted")}
        ${scenarioCard("Scope", auth?.user.locationId ? "Location scoped" : "All locations", "status-muted")}
        ${scenarioCard("Ledger", "No manual movement endpoint", "status-ok")}
      </div>
    </section>

    <section class="catalog-layout">
      <aside class="catalog-side">
        <section class="band compact-band">
          <div class="section-head"><h2>Filters</h2><button id="inventory-refresh" class="button secondary" type="button">Refresh</button></div>
          <div class="field"><label for="inventory-location">Location</label><select id="inventory-location" class="select"><option value="">All available</option></select></div>
          <div class="field"><label for="inventory-sku">SKU ID</label><input id="inventory-sku" class="input" placeholder="Optional SKU GUID"></div>
          <label class="check-field"><input id="inventory-include-empty" type="checkbox"><span>Show empty batches</span></label>
        </section>
        <section class="band compact-band">
          <h2>Locations</h2>
          <div id="inventory-locations" class="reference-list"><span class="muted-text">Loading</span></div>
        </section>
      </aside>

      <section class="catalog-main">
        <section class="band">
          <div class="section-head"><h2>Stock balances</h2><span id="inventory-balance-count" class="muted-text">Loading</span></div>
          <div class="table-wrap"><table><thead><tr><th>Location</th><th>SKU</th><th>Available</th><th>Reserved</th><th>Target</th><th>Updated</th>${canWrite ? "<th>Actions</th>" : ""}</tr></thead><tbody id="inventory-balances"><tr><td colspan="${canWrite ? 7 : 6}">Loading stock</td></tr></tbody></table></div>
        </section>
        <section class="catalog-detail-grid">
          <section class="band">
            <div class="section-head"><h2>Batches</h2><span id="inventory-batch-count" class="muted-text">Loading</span></div>
            <div class="table-wrap"><table><thead><tr><th>Lot</th><th>Location</th><th>SKU</th><th>Qty</th><th>Expiry date</th><th>Notes</th></tr></thead><tbody id="inventory-batches"><tr><td colspan="6">Loading batches</td></tr></tbody></table></div>
          </section>
          <section class="band">
            <div class="section-head"><h2>Transactions</h2><span id="inventory-transaction-count" class="muted-text">Loading</span></div>
            <div class="table-wrap"><table><thead><tr><th>Type</th><th>Location</th><th>SKU</th><th>Change</th><th>Created</th></tr></thead><tbody id="inventory-transactions"><tr><td colspan="5">Loading transactions</td></tr></tbody></table></div>
          </section>
        </section>
      </section>
    </section>`;

  document.getElementById("inventory-refresh").addEventListener("click", refreshInventoryWorkspace);
  document.getElementById("inventory-location").addEventListener("change", () => {
    selectedInventoryLocationId = document.getElementById("inventory-location").value;
    refreshInventoryTables();
  });
  document.getElementById("inventory-sku").addEventListener("input", debounce(refreshInventoryTables, 300));
  document.getElementById("inventory-include-empty").addEventListener("change", loadInventoryBatches);
  refreshInventoryWorkspace();
}

async function refreshInventoryWorkspace() {
  await loadInventoryLocations();
  await refreshInventoryTables();
}

async function refreshInventoryTables() {
  await Promise.all([
    loadInventoryBalances(),
    loadInventoryBatches(),
    loadInventoryTransactions()
  ]);
}

async function loadInventoryLocations() {
  const select = document.getElementById("inventory-location");
  const list = document.getElementById("inventory-locations");
  try {
    inventoryLocations = await request("/api/v1/inventory/locations");
    select.innerHTML = `<option value="">All available</option>${inventoryLocations.map((location) => `<option value="${escapeHtml(location.id)}">${escapeHtml(location.name)}</option>`).join("")}`;
    select.value = selectedInventoryLocationId;
    list.innerHTML = inventoryLocations.length === 0
      ? `<span class="muted-text">No locations</span>`
      : inventoryLocations.map((location) => `<button class="reference-item" type="button" data-location-id="${escapeHtml(location.id)}"><strong>${escapeHtml(location.name)}</strong><span>${escapeHtml(location.locationType)} ${location.isActive ? "Active" : "Inactive"}</span></button>`).join("");
    list.querySelectorAll("[data-location-id]").forEach((button) => button.addEventListener("click", () => {
      selectedInventoryLocationId = button.dataset.locationId;
      select.value = selectedInventoryLocationId;
      refreshInventoryTables();
    }));
  } catch (exception) {
    list.innerHTML = `<span class="muted-text">${escapeHtml(getFriendlyInventoryError(exception))}</span>`;
  }
}

async function loadInventoryBalances() {
  const auth = getAuth();
  const canWrite = auth?.user.role === "Admin";
  const tbody = document.getElementById("inventory-balances");
  const count = document.getElementById("inventory-balance-count");
  const params = inventoryParams();
  params.set("pageSize", "50");
  try {
    const result = await request(`/api/v1/inventory/stock-balances?${params.toString()}`);
    count.textContent = `${result.totalCount} balance${result.totalCount === 1 ? "" : "s"}`;
    tbody.innerHTML = result.items.length === 0
      ? `<tr><td colspan="${canWrite ? 7 : 6}">No stock balances yet.</td></tr>`
      : result.items.map((balance) => `
        <tr>
          <td>${escapeHtml(balance.locationName)}</td>
          <td><code>${escapeHtml(balance.skuId)}</code></td>
          <td>${escapeHtml(balance.availableQty)}</td>
          <td>${escapeHtml(balance.reservedInWarehouseQty + balance.reservedWithRepQty)}</td>
          <td>${escapeHtml(balance.targetQty ?? "-")}</td>
          <td>${escapeHtml(formatDateTime(balance.lastUpdated))}</td>
          ${canWrite ? `<td><button class="button secondary table-action" type="button" data-target-location="${escapeHtml(balance.locationId)}" data-target-sku="${escapeHtml(balance.skuId)}" data-target-current="${escapeHtml(balance.targetQty ?? "")}">Set target</button></td>` : ""}
        </tr>`).join("");
    tbody.querySelectorAll("[data-target-location]").forEach((button) => button.addEventListener("click", () => setInventoryTarget(button)));
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="${canWrite ? 7 : 6}">${escapeHtml(getFriendlyInventoryError(exception))}</td></tr>`;
  }
}

async function loadInventoryBatches() {
  const tbody = document.getElementById("inventory-batches");
  const count = document.getElementById("inventory-batch-count");
  const params = inventoryParams();
  params.set("pageSize", "50");
  params.set("includeEmpty", String(document.getElementById("inventory-include-empty").checked));
  try {
    const result = await request(`/api/v1/inventory/batches?${params.toString()}`);
    count.textContent = `${result.totalCount} batch${result.totalCount === 1 ? "" : "es"}`;
    tbody.innerHTML = result.items.length === 0
      ? `<tr><td colspan="6">No batches yet.</td></tr>`
      : result.items.map((batch) => `
        <tr>
          <td>${escapeHtml(batch.lotNumber || "-")}</td>
          <td>${escapeHtml(batch.locationName)}</td>
          <td><code>${escapeHtml(batch.skuId)}</code></td>
          <td>${escapeHtml(batch.quantity)}</td>
          <td>${escapeHtml(batch.expiryDate || "-")}</td>
          <td>${escapeHtml(batch.notes || "-")}</td>
        </tr>`).join("");
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="6">${escapeHtml(getFriendlyInventoryError(exception))}</td></tr>`;
  }
}

async function loadInventoryTransactions() {
  const tbody = document.getElementById("inventory-transactions");
  const count = document.getElementById("inventory-transaction-count");
  const params = inventoryParams();
  params.set("pageSize", "50");
  try {
    const result = await request(`/api/v1/inventory/transactions?${params.toString()}`);
    count.textContent = `${result.totalCount} transaction${result.totalCount === 1 ? "" : "s"}`;
    tbody.innerHTML = result.items.length === 0
      ? `<tr><td colspan="5">No transactions yet.</td></tr>`
      : result.items.map((transaction) => `
        <tr>
          <td>${escapeHtml(transaction.transactionType)}</td>
          <td>${escapeHtml(transaction.locationName)}</td>
          <td><code>${escapeHtml(transaction.skuId)}</code></td>
          <td>${escapeHtml(transaction.quantityChange)}</td>
          <td>${escapeHtml(formatDateTime(transaction.createdAt))}</td>
        </tr>`).join("");
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="5">${escapeHtml(getFriendlyInventoryError(exception))}</td></tr>`;
  }
}

function inventoryParams() {
  const params = new URLSearchParams();
  const locationId = document.getElementById("inventory-location")?.value;
  const skuId = document.getElementById("inventory-sku")?.value.trim();
  if (locationId) {
    params.set("locationId", locationId);
  }
  if (skuId) {
    params.set("skuId", skuId);
  }
  return params;
}

async function setInventoryTarget(button) {
  const current = button.dataset.targetCurrent || "";
  const raw = window.prompt("Target quantity", current);
  if (raw === null) {
    return;
  }
  const value = raw.trim();
  if (value && (!Number.isInteger(Number(value)) || Number(value) < 0)) {
    notice("Target quantity must be a non-negative whole number.", "error");
    return;
  }

  try {
    await request(`/api/v1/inventory/stock-balances/${button.dataset.targetLocation}/${button.dataset.targetSku}/target`, {
      method: "PUT",
      body: JSON.stringify({ targetQty: value ? Number(value) : null })
    });
    notice("Target quantity updated.", "success");
    await loadInventoryBalances();
  } catch (exception) {
    notice(getFriendlyInventoryError(exception), "error");
  }
}

async function renderCrm() {
  const auth = getAuth();
  const canWrite = auth?.user.role === "Admin";
  document.getElementById("view").innerHTML = `
    <section class="band">
      <div class="section-head">
        <div>
          <h2>Merchants</h2>
          <p>Profiles, representatives, and notes used by operation drafts.</p>
        </div>
        <span id="crm-count" class="status-pill status-muted">Loading</span>
      </div>
      ${canWrite ? `
        <form id="merchant-form" class="form grid-form">
          <div class="field"><label for="merchant-name">Business name</label><input id="merchant-name" class="input" required></div>
          <div class="field"><label for="merchant-contact">Contact person</label><input id="merchant-contact" class="input" required></div>
          <div class="field"><label for="merchant-phone">Phone</label><input id="merchant-phone" class="input"></div>
          <div class="field"><label for="merchant-type">Business type</label><select id="merchant-type" class="select"><option>Wholesale</option><option>Retail</option><option>Online</option></select></div>
          <button class="button primary" type="submit">Create merchant</button>
        </form>` : ""}
      <div class="table-wrap">
        <table><thead><tr><th>Business</th><th>Contact</th><th>Phone</th><th>Type</th><th>Status</th><th>Notes</th></tr></thead><tbody id="merchant-rows"></tbody></table>
      </div>
    </section>
    <section class="band">
      <div class="section-head"><h2>Representatives</h2><span id="rep-count" class="status-pill status-muted">Loading</span></div>
      ${canWrite ? `
        <form id="rep-form" class="form grid-form">
          <div class="field"><label for="rep-name">Name</label><input id="rep-name" class="input" required></div>
          <div class="field"><label for="rep-phone">Phone</label><input id="rep-phone" class="input"></div>
          <div class="field"><label for="rep-type">Type</label><select id="rep-type" class="select"><option>External</option><option>Internal</option></select></div>
          <button class="button primary" type="submit">Create representative</button>
        </form>` : ""}
      <div class="table-wrap">
        <table><thead><tr><th>Name</th><th>Phone</th><th>Type</th><th>Status</th></tr></thead><tbody id="rep-rows"></tbody></table>
      </div>
    </section>`;

  if (canWrite) {
    document.getElementById("merchant-form").addEventListener("submit", createMerchant);
    document.getElementById("rep-form").addEventListener("submit", createRepresentative);
  }
  await Promise.all([loadMerchants(), loadRepresentatives()]);
}

async function loadMerchants() {
  const tbody = document.getElementById("merchant-rows");
  const count = document.getElementById("crm-count");
  try {
    const result = await request("/api/v1/crm/merchants?includeInactive=true&pageSize=50");
    count.textContent = `${result.totalCount} merchants`;
    tbody.innerHTML = result.items.length === 0 ? `<tr><td colspan="6">No merchants yet.</td></tr>` : result.items.map((merchant) => `
      <tr>
        <td>${escapeHtml(merchant.businessName)}</td>
        <td>${escapeHtml(merchant.contactPersonName)}</td>
        <td>${escapeHtml((merchant.phoneNumbers || []).join(", ") || "-")}</td>
        <td>${escapeHtml(merchant.businessType)}</td>
        <td><span class="status-pill ${merchant.status === "Active" ? "status-ok" : "status-muted"}">${escapeHtml(merchant.status)}</span></td>
        <td>
          <button class="button secondary table-action" type="button" data-note-merchant="${escapeHtml(merchant.id)}">Add note</button>
          <button class="button secondary table-action" type="button" data-eligibility-merchant="${escapeHtml(merchant.id)}">Eligibility</button>
        </td>
      </tr>`).join("");
    tbody.querySelectorAll("[data-note-merchant]").forEach((button) => button.addEventListener("click", () => addMerchantNote(button.dataset.noteMerchant)));
    tbody.querySelectorAll("[data-eligibility-merchant]").forEach((button) => button.addEventListener("click", () => showMerchantEligibility(button.dataset.eligibilityMerchant)));
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="6">${escapeHtml(getFriendlyWorkspaceError(exception))}</td></tr>`;
  }
}

async function loadRepresentatives() {
  const tbody = document.getElementById("rep-rows");
  const count = document.getElementById("rep-count");
  try {
    const reps = await request("/api/v1/crm/representatives?includeInactive=true");
    count.textContent = `${reps.length} reps`;
    tbody.innerHTML = reps.length === 0 ? `<tr><td colspan="4">No representatives yet.</td></tr>` : reps.map((rep) => `
      <tr><td>${escapeHtml(rep.name)}</td><td>${escapeHtml((rep.phoneNumbers || []).join(", ") || "-")}</td><td>${escapeHtml(rep.type)}</td><td>${escapeHtml(rep.status)}</td></tr>`).join("");
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="4">${escapeHtml(getFriendlyWorkspaceError(exception))}</td></tr>`;
  }
}

async function createMerchant(event) {
  event.preventDefault();
  const businessName = document.getElementById("merchant-name").value.trim();
  const contactPersonName = document.getElementById("merchant-contact").value.trim();
  if (!businessName || !contactPersonName) {
    notice("Business name and contact person are required.", "error");
    return;
  }

  try {
    await request("/api/v1/crm/merchants", {
      method: "POST",
      body: JSON.stringify({
        businessName,
        contactPersonName,
        phoneNumbers: document.getElementById("merchant-phone").value.trim() ? [document.getElementById("merchant-phone").value.trim()] : [],
        businessType: document.getElementById("merchant-type").value
      })
    });
    event.target.reset();
    notice("Merchant created.", "success");
    await loadMerchants();
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

async function createRepresentative(event) {
  event.preventDefault();
  const name = document.getElementById("rep-name").value.trim();
  if (!name) {
    notice("Representative name is required.", "error");
    return;
  }

  try {
    await request("/api/v1/crm/representatives", {
      method: "POST",
      body: JSON.stringify({
        name,
        phoneNumbers: document.getElementById("rep-phone").value.trim() ? [document.getElementById("rep-phone").value.trim()] : [],
        type: document.getElementById("rep-type").value
      })
    });
    event.target.reset();
    notice("Representative created.", "success");
    await loadRepresentatives();
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

async function showMerchantEligibility(merchantId) {
  try {
    const rows = await request(`/api/v1/crm/merchants/${merchantId}/eligibility`);
    if (rows.length === 0) {
      notice("No confirmed merchant sales are eligible for return yet.", "info");
      return;
    }

    notice(rows.map((row) => `SKU ${row.skuId}: returnable ${row.returnableQty} / sold ${row.soldQty}`).join(" | "), "info");
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

async function addMerchantNote(merchantId) {
  const note = window.prompt("Merchant note");
  if (!note?.trim()) {
    return;
  }
  try {
    await request(`/api/v1/crm/merchants/${merchantId}/notes`, { method: "POST", body: JSON.stringify({ note }) });
    notice("Note added.", "success");
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

async function renderOperations() {
  const auth = getAuth();
  const canWrite = ["Admin", "WarehouseClerk"].includes(auth?.user.role);
  document.getElementById("view").innerHTML = `
    <section class="band">
      <div class="section-head">
        <div><h2>Draft operation</h2><p>Create receipts, sales, reserves, supply, write-off, change, and return flows.</p></div>
        <span id="operation-count" class="status-pill status-muted">Loading</span>
      </div>
      ${canWrite ? `
        <form id="operation-form" class="form grid-form">
          <div class="field"><label for="op-type">Type</label><select id="op-type" class="select"><option>InventoryReceipt</option><option>WholesaleSale</option><option>RetailSale</option><option>Reserve</option><option>Supply</option><option>WriteOff</option><option>Change</option><option>Return</option></select></div>
          <div class="field"><label for="op-client">Merchant/client</label><select id="op-client" class="select"></select></div>
          <div class="field"><label for="op-source">Source location</label><select id="op-source" class="select"></select></div>
          <div class="field"><label for="op-destination">Destination location</label><select id="op-destination" class="select"></select></div>
          <div class="field"><label for="op-sku">SKU</label><select id="op-sku" class="select"></select></div>
          <div class="field"><label for="op-qty">Quantity</label><input id="op-qty" class="input" type="number" min="1" value="1"></div>
          <div class="field"><label for="op-section">Line section</label><select id="op-section" class="select"><option>Standard</option><option>WithRep</option><option>Return</option><option>ChangeOut</option><option>ChangeIn</option></select></div>
          <div class="field"><label for="op-sku-2">Second SKU</label><select id="op-sku-2" class="select"></select></div>
          <div class="field"><label for="op-qty-2">Second qty</label><input id="op-qty-2" class="input" type="number" min="0" value="0"></div>
          <div class="field"><label for="op-section-2">Second section</label><select id="op-section-2" class="select"><option>ChangeIn</option><option>ChangeOut</option><option>Return</option><option>Standard</option></select></div>
          <div class="field"><label for="op-expiry">Receipt expiry</label><input id="op-expiry" class="input" type="date"></div>
          <div class="field"><label for="op-lot">Receipt lot</label><input id="op-lot" class="input"></div>
          <button class="button primary" type="submit">Save draft</button>
        </form>` : `<p>This role can inspect operations but cannot create drafts.</p>`}
      <div class="table-wrap">
        <table><thead><tr><th>No.</th><th>Type</th><th>Status</th><th>Client</th><th>Created</th><th>Action</th></tr></thead><tbody id="operation-rows"></tbody></table>
      </div>
    </section>`;

  if (canWrite) {
    await Promise.all([hydrateOperationLocations(), hydrateOperationSkus(), hydrateOperationMerchants()]);
    document.getElementById("operation-form").addEventListener("submit", createOperationDraft);
  }
  await loadOperations();
}

async function hydrateOperationLocations() {
  const locations = await request("/api/v1/inventory/locations");
  for (const id of ["op-source", "op-destination"]) {
    const select = document.getElementById(id);
    select.innerHTML = `<option value="">-</option>${locations.map((location) => `<option value="${escapeHtml(location.id)}">${escapeHtml(location.name)}</option>`).join("")}`;
  }
}

async function hydrateOperationSkus() {
  const products = await request("/api/v1/catalog/products?includeInactive=false&pageSize=100");
  const skus = [];
  for (const product of products.items || []) {
    const detail = await request(`/api/v1/catalog/products/${product.id}`);
    for (const sku of detail.skus.filter((value) => value.isActive)) {
      skus.push({ id: sku.id, label: `${detail.name} / ${sku.skuCode}` });
    }
  }
  operationSkuOptions = skus;
  const options = skus.map((sku) => `<option value="${escapeHtml(sku.id)}">${escapeHtml(sku.label)}</option>`).join("");
  document.getElementById("op-sku").innerHTML = options;
  document.getElementById("op-sku-2").innerHTML = `<option value="">-</option>${options}`;
}

async function hydrateOperationMerchants() {
  try {
    const result = await request("/api/v1/crm/merchants?includeInactive=false&pageSize=100");
    document.getElementById("op-client").innerHTML = `<option value="">-</option>${(result.items || []).map((merchant) => `<option value="${escapeHtml(merchant.id)}">${escapeHtml(merchant.businessName)}</option>`).join("")}`;
  } catch {
    document.getElementById("op-client").innerHTML = `<option value="">CRM unavailable</option>`;
  }
}

async function loadOperations() {
  const tbody = document.getElementById("operation-rows");
  const count = document.getElementById("operation-count");
  const auth = getAuth();
  const canWrite = ["Admin", "WarehouseClerk"].includes(auth?.user.role);
  try {
    const result = await request("/api/v1/operations?pageSize=50");
    count.textContent = `${result.totalCount} operations`;
    tbody.innerHTML = result.items.length === 0 ? `<tr><td colspan="6">No operations yet.</td></tr>` : result.items.map((operation) => `
      <tr>
        <td>${escapeHtml(operation.operationNumber)}</td>
        <td>${escapeHtml(operation.operationType)}</td>
        <td><span class="status-pill ${operation.status === "Confirmed" ? "status-ok" : "status-muted"}">${escapeHtml(operation.status)}</span></td>
        <td>${escapeHtml(operation.clientName || "-")}</td>
        <td>${escapeHtml(formatDateTime(operation.createdAt))}</td>
        <td>${canWrite && operation.status === "Draft" ? `<button class="button secondary table-action" type="button" data-confirm-op="${escapeHtml(operation.id)}">Confirm</button>` : "-"}</td>
      </tr>`).join("");
    tbody.querySelectorAll("[data-confirm-op]").forEach((button) => button.addEventListener("click", () => confirmOperation(button.dataset.confirmOp)));
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="6">${escapeHtml(getFriendlyWorkspaceError(exception))}</td></tr>`;
  }
}

async function createOperationDraft(event) {
  event.preventDefault();
  const type = document.getElementById("op-type").value;
  const quantity = Number(document.getElementById("op-qty").value);
  const validationMessage = validateOperationForm(type, quantity);
  if (validationMessage) {
    notice(validationMessage, "error");
    return;
  }

  const body = {
    operationType: type,
    sourceLocationId: document.getElementById("op-source").value || null,
    destinationLocationId: document.getElementById("op-destination").value || null,
    clientId: document.getElementById("op-client").value || null,
    receipt: type === "InventoryReceipt" ? { supplierName: "Manual test", invoiceNumber: document.getElementById("op-lot").value || null } : null,
    lines: [{
      skuId: document.getElementById("op-sku").value,
      quantity,
      section: document.getElementById("op-section").value,
      expiryDate: document.getElementById("op-expiry").value || null,
      lotNumber: document.getElementById("op-lot").value || null
    }]
  };

  const secondSkuId = document.getElementById("op-sku-2").value;
  const secondQuantity = Number(document.getElementById("op-qty-2").value);
  if (secondSkuId && secondQuantity > 0) {
    body.lines.push({
      skuId: secondSkuId,
      quantity: secondQuantity,
      section: document.getElementById("op-section-2").value,
      expiryDate: document.getElementById("op-expiry").value || null,
      lotNumber: document.getElementById("op-lot").value || null
    });
  }

  if (type === "InventoryReceipt" && !body.destinationLocationId && body.sourceLocationId) {
    body.destinationLocationId = body.sourceLocationId;
  }

  try {
    await request("/api/v1/operations", { method: "POST", body: JSON.stringify(body) });
    notice("Draft saved.", "success");
    await loadOperations();
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

function validateOperationForm(type, quantity) {
  const source = document.getElementById("op-source").value;
  const destination = document.getElementById("op-destination").value;
  const merchant = document.getElementById("op-client").value;
  const section = document.getElementById("op-section").value;
  const secondSku = document.getElementById("op-sku-2").value;
  const secondQuantity = Number(document.getElementById("op-qty-2").value);
  const secondSection = document.getElementById("op-section-2").value;

  if (!document.getElementById("op-sku").value) {
    return "Select a SKU.";
  }
  if (!Number.isInteger(quantity) || quantity < 1) {
    return "Quantity must be a whole number greater than zero.";
  }
  if (secondQuantity && (!Number.isInteger(secondQuantity) || secondQuantity < 0)) {
    return "Second quantity must be a non-negative whole number.";
  }
  if (["WholesaleSale", "RetailSale", "Return", "Change"].includes(type) && !merchant) {
    return `${type} requires a merchant/client.`;
  }
  if (["WholesaleSale", "RetailSale", "Reserve", "WriteOff"].includes(type) && !source) {
    return `${type} requires a source location.`;
  }
  if (type === "Supply" && (!source || !destination)) {
    return "Supply requires both source and destination locations.";
  }
  if (type === "InventoryReceipt" && !destination && !source) {
    return "Inventory receipt requires a receiving location.";
  }
  if (type === "Return") {
    if (section === "ChangeIn" || secondSection === "ChangeIn") {
      return "Return is one-sided and cannot include replacement lines.";
    }
    if (section !== "Return") {
      return "Return lines must use the Return section.";
    }
  }
  if (type === "Change") {
    const sections = [section];
    if (secondSku && secondQuantity > 0) {
      sections.push(secondSection);
    }
    if (!sections.includes("ChangeOut") || !sections.includes("ChangeIn")) {
      return "Change requires one ChangeOut line and one ChangeIn line.";
    }
  }

  return "";
}

async function confirmOperation(operationId) {
  try {
    await request(`/api/v1/operations/${operationId}/confirm`, { method: "POST" });
    notice("Operation confirmed and inventory updated.", "success");
    await loadOperations();
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

async function renderNotifications() {
  const auth = getAuth();
  const isAdmin = auth?.user.role === "Admin";
  document.getElementById("view").innerHTML = `
    <section class="band">
      <div class="section-head"><h2>Notifications</h2><span id="notification-count" class="status-pill status-muted">Loading</span></div>
      <div class="toolbar"><button id="mark-all-read" class="button secondary" type="button">Mark all read</button></div>
      <div class="table-wrap"><table><thead><tr><th>Type</th><th>Message</th><th>Target</th><th>Created</th><th>Status</th></tr></thead><tbody id="notification-rows"></tbody></table></div>
    </section>
    ${isAdmin ? `
      <section class="band">
        <h2>Manual alert triggers</h2>
        <div class="toolbar">
          <button class="button secondary" type="button" data-alert-run="low-stock">Low stock</button>
          <button class="button secondary" type="button" data-alert-run="expiry">Expiry</button>
          <button class="button secondary" type="button" data-alert-run="unresolved-reserves">Unresolved reserves</button>
          <button class="button secondary" type="button" data-alert-run="outstanding-balances">Outstanding balances</button>
        </div>
      </section>` : ""}`;

  document.getElementById("mark-all-read").addEventListener("click", markNotificationsRead);
  document.querySelectorAll("[data-alert-run]").forEach((button) => button.addEventListener("click", () => runAlert(button.dataset.alertRun)));
  await loadNotifications();
}

async function loadNotifications() {
  const tbody = document.getElementById("notification-rows");
  const count = document.getElementById("notification-count");
  try {
    const result = await request("/api/v1/notifications?pageSize=50");
    count.textContent = `${result.totalCount} visible`;
    tbody.innerHTML = result.items.length === 0 ? `<tr><td colspan="5">No notifications yet.</td></tr>` : result.items.map((item) => `
      <tr>
        <td>${escapeHtml(item.alertType)}</td>
        <td>${escapeHtml(item.message)}</td>
        <td>${escapeHtml(item.targetRole || item.targetUserId || "-")}</td>
        <td>${escapeHtml(formatDateTime(item.createdAt))}</td>
        <td>${item.isRead ? "Read" : `<button class="button secondary table-action" type="button" data-read-notification="${escapeHtml(item.id)}">Mark read</button>`}</td>
      </tr>`).join("");
    tbody.querySelectorAll("[data-read-notification]").forEach((button) => button.addEventListener("click", () => markNotificationRead(button.dataset.readNotification)));
  } catch (exception) {
    count.textContent = "Failed";
    tbody.innerHTML = `<tr><td colspan="5">${escapeHtml(getFriendlyWorkspaceError(exception))}</td></tr>`;
  }
}

async function markNotificationRead(id) {
  await request(`/api/v1/notifications/${id}/read`, { method: "PATCH" });
  await loadNotifications();
}

async function markNotificationsRead() {
  await request("/api/v1/notifications/read-all", { method: "PATCH" });
  await loadNotifications();
}

async function runAlert(name) {
  try {
    const result = await request(`/api/v1/alerts/run/${name}`, { method: "POST" });
    notice(`Alert run matched ${result.matchedItems} item(s).`, "success");
    await loadNotifications();
  } catch (exception) {
    notice(getFriendlyWorkspaceError(exception), "error");
  }
}

function renderListPattern(title, headers) {
  document.getElementById("view").innerHTML = `<section class="band"><h2>${escapeHtml(title)}</h2><div class="table-wrap"><table><thead><tr>${headers.map((header) => `<th>${escapeHtml(header)}</th>`).join("")}</tr></thead><tbody><tr>${headers.map(() => "<td>Pending API</td>").join("")}</tr></tbody></table></div></section>`;
}

function renderForbidden() {
  document.getElementById("page-title").textContent = "Forbidden";
  document.getElementById("route-label").textContent = "Authorization";
  renderNav(getAuth());
  renderSession(getAuth());
  document.getElementById("view").innerHTML = `<section class="band"><h2>Access denied</h2><p>This session cannot open that workspace.</p></section>`;
}

async function logout() {
  const auth = getAuth();
  try {
    if (auth?.refreshToken) {
      await request("/api/v1/auth/logout", { method: "POST", body: JSON.stringify({ refreshToken: auth.refreshToken }) });
    }
  } finally {
    clearAuth();
    location.hash = "/login";
  }
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;" })[character]);
}

function roleLabel(role) {
  return { CLevel: "C-Level", WarehouseClerk: "Warehouse Clerk" }[role] || role;
}

function formatPackHint(product) {
  return product.piecesPerPack ? `${escapeHtml(product.sellMode || "Pack")} / ${escapeHtml(product.piecesPerPack)} pcs` : escapeHtml(product.sellMode || "-");
}

function formatPower(sku) {
  return sku.powerValue === null || sku.powerValue === undefined ? "-" : `${sku.powerSign || ""}${sku.powerValue}`;
}

function formatDateTime(value) {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
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

function getFriendlyApiError(exception) {
  const status = exception?.status;
  if (status === 401) {
    return "Session expired. Sign in again.";
  }
  if (status === 403) {
    return "You do not have access to this catalog action.";
  }
  return "Could not load catalog data.";
}

function getFriendlyCatalogWriteError(exception) {
  const message = exception instanceof Error ? exception.message : "";
  if (message.includes("errors")) {
    try {
      const body = JSON.parse(message);
      return Object.values(body.errors || {}).flat().join(" ") || "Check the catalog form values.";
    } catch {
      return "Check the catalog form values.";
    }
  }
  if (exception?.status === 409 || message.includes("Conflict")) {
    return "That SKU code already exists.";
  }
  if (exception?.status === 403) {
    return "You do not have permission to change catalog data.";
  }
  return "Catalog change failed.";
}

function getFriendlyInventoryError(exception) {
  const status = exception?.status;
  if (status === 401) {
    return "Session expired. Sign in again.";
  }
  if (status === 403) {
    return "You do not have access to this inventory action.";
  }
  if (status === 400) {
    return "Check the inventory filters or target quantity.";
  }
  return "Could not load inventory data.";
}

function getFriendlyWorkspaceError(exception) {
  const message = exception instanceof Error ? exception.message : "";
  if (message.includes("errors")) {
    try {
      const body = JSON.parse(message);
      return Object.values(body.errors || {}).flat().join(" ") || "Check the form values.";
    } catch {
      return "Check the form values.";
    }
  }
  if (exception?.status === 401) {
    return "Session expired. Sign in again.";
  }
  if (exception?.status === 403) {
    return "This account does not have permission for that action.";
  }
  if (exception?.status === 400) {
    return "Check the request values.";
  }
  return "The workspace request failed.";
}

function debounce(callback, delay) {
  let timeout;
  return (...args) => {
    window.clearTimeout(timeout);
    timeout = window.setTimeout(() => callback(...args), delay);
  };
}
