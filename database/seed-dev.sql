-- PRD v2.5 development seed.
-- Passwords:
--   admin / Admin123!
--   clevel / CLevel123!
--   accountant / Accountant123!
--   roxy_clerk / Clerk123!
--   retail_clerk / Clerk123!
--   online_clerk / Clerk123!

insert into inventory.locations (id, name, location_type, is_active)
values
  ('11111111-1111-1111-1111-111111111111', 'Roxy (Main)', 'MainWarehouse', true),
  ('22222222-2222-2222-2222-222222222222', 'Mohamed Naguib (Retail)', 'SubWarehouse', true),
  ('33333333-3333-3333-3333-333333333333', 'Online', 'Online', true)
on conflict (id) do update
set name = excluded.name,
    location_type = excluded.location_type,
    is_active = excluded.is_active;

insert into shared.system_settings (key, value, description)
values
  ('low_stock_threshold_default', '10', 'Default low stock alert threshold (pieces)'),
  ('reserve_unresolved_days', '7', 'Days before an unresolved reserve triggers alert'),
  ('in_warehouse_expiry_months', '3', 'Months before expiry to fire in-warehouse alert'),
  ('merchant_held_expiry_months', '18', 'Months before expiry to fire merchant-held alert'),
  ('outstanding_balance_days', '30', 'Days since last payment to fire balance notification flags')
on conflict (key) do update
set value = excluded.value,
    description = excluded.description,
    updated_at = current_timestamp;

insert into identity.roles_permissions (id, role, permission)
values
  (uuid_generate_v4(), 'CLevel', 'catalog.read'),
  (uuid_generate_v4(), 'CLevel', 'inventory.read'),
  (uuid_generate_v4(), 'CLevel', 'operations.read'),
  (uuid_generate_v4(), 'CLevel', 'payments.read'),
  (uuid_generate_v4(), 'CLevel', 'reports.read'),
  (uuid_generate_v4(), 'Admin', 'users.read'),
  (uuid_generate_v4(), 'Admin', 'users.write'),
  (uuid_generate_v4(), 'Admin', 'catalog.read'),
  (uuid_generate_v4(), 'Admin', 'catalog.write'),
  (uuid_generate_v4(), 'Admin', 'inventory.read'),
  (uuid_generate_v4(), 'Admin', 'inventory.write'),
  (uuid_generate_v4(), 'Admin', 'operations.read'),
  (uuid_generate_v4(), 'Admin', 'operations.write'),
  (uuid_generate_v4(), 'Admin', 'payments.read'),
  (uuid_generate_v4(), 'Admin', 'payments.write'),
  (uuid_generate_v4(), 'Admin', 'payments.draft'),
  (uuid_generate_v4(), 'Admin', 'reports.read'),
  (uuid_generate_v4(), 'Admin', 'audit.read'),
  (uuid_generate_v4(), 'Admin', 'settings.write'),
  (uuid_generate_v4(), 'Accountant', 'operations.read'),
  (uuid_generate_v4(), 'Accountant', 'payments.read'),
  (uuid_generate_v4(), 'Accountant', 'payments.draft'),
  (uuid_generate_v4(), 'Accountant', 'reports.read'),
  (uuid_generate_v4(), 'WarehouseClerk', 'catalog.read'),
  (uuid_generate_v4(), 'WarehouseClerk', 'inventory.read'),
  (uuid_generate_v4(), 'WarehouseClerk', 'operations.read'),
  (uuid_generate_v4(), 'WarehouseClerk', 'operations.write')
on conflict (role, permission) do nothing;

insert into identity.users (id, username, password_hash, full_name, role, location_id, is_active)
values
  (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1',
    'admin',
    '<hash:Admin123!>',
    'Lansee Admin',
    'Admin',
    null,
    true
  ),
  (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2',
    'clevel',
    '<hash:CLevel123!>',
    'C-Level Executive',
    'CLevel',
    null,
    true
  ),
  (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3',
    'accountant',
    '<hash:Accountant123!>',
    'Lansee Accountant',
    'Accountant',
    null,
    true
  ),
  (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4',
    'roxy_clerk',
    '<hash:Clerk123!>',
    'Roxy Warehouse Clerk',
    'WarehouseClerk',
    '11111111-1111-1111-1111-111111111111',
    true
  ),
  (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa5',
    'retail_clerk',
    '<hash:Clerk123!>',
    'Retail Warehouse Clerk',
    'WarehouseClerk',
    '22222222-2222-2222-2222-222222222222',
    true
  ),
  (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa6',
    'online_clerk',
    '<hash:Clerk123!>',
    'Online Warehouse Clerk',
    'WarehouseClerk',
    '33333333-3333-3333-3333-333333333333',
    true
  )
on conflict (username) do update
set password_hash = excluded.password_hash,
    full_name = excluded.full_name,
    role = excluded.role,
    location_id = excluded.location_id,
    is_active = excluded.is_active;

insert into catalog.categories (id, parent_id, name)
values
  ('10000000-0000-0000-0000-000000000001', null, 'Products'),
  ('10000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'Lenses'),
  ('10000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000002', 'Colored Lenses'),
  ('10000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000002', 'Medical Lenses'),
  ('10000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000004', 'Plain Medical'),
  ('10000000-0000-0000-0000-000000000006', '10000000-0000-0000-0000-000000000004', 'Colored Medical'),
  ('10000000-0000-0000-0000-000000000007', '10000000-0000-0000-0000-000000000001', 'Solutions'),
  ('10000000-0000-0000-0000-000000000008', '10000000-0000-0000-0000-000000000007', 'Preservation / Conservative Solution')
on conflict (id) do update
set parent_id = excluded.parent_id,
    name = excluded.name;
