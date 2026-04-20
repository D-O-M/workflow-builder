export interface AppNavItem {
  id: string;
  label: string;
  route: string;
  icon: string;
  exact?: boolean;
}

export const APP_PRIMARY_NAV_ITEMS: AppNavItem[] = [
  { id: 'dashboard', label: 'Dashboard', route: '/dashboard', icon: 'dashboard', exact: true },
  { id: 'blueprints', label: 'Blueprints', route: '/blueprints', icon: 'account_tree', exact: true },
  { id: 'start-process', label: 'Start Process', route: '/processes/new', icon: 'play_circle', exact: true },
  { id: 'processes', label: 'Processes', route: '/processes', icon: 'receipt_long', exact: true },
];
