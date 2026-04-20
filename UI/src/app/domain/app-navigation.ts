export interface AppNavItem {
  id: string;
  label: string;
  route: string;
  icon: string;
  exact?: boolean;
}

export const APP_PRIMARY_NAV_ITEMS: AppNavItem[] = [
  { id: 'dashboard', label: 'Dashboard', route: '/dashboard', icon: 'dashboard', exact: true },
  { id: 'workflows', label: 'Workflows', route: '/blueprints', icon: 'account_tree', exact: true },
  { id: 'trigger', label: 'Trigger', route: '/execute', icon: 'play_circle', exact: true },
  { id: 'executions', label: 'Executions', route: '/executions', icon: 'receipt_long', exact: true },
];
