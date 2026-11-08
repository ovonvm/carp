import { createRouter, createWebHistory } from 'vue-router'
import type { RouteLocationNormalized } from 'vue-router'
import { useAuthStore } from './stores/auth'
import { useSiteStore } from './stores/site'

declare module 'vue-router' {
  interface RouteMeta {
    requiresAuth?: boolean
    guestOnly?: boolean
    layout?: 'shell'
    adminOnly?: boolean
    /** System admin or a user with `can_create_groups` (P1 group console). */
    groupsAdmin?: boolean
    /** Browser tab label; the site name is appended by the afterEach hook. */
    title?: string
  }
}

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', redirect: '/files' },
    {
      path: '/login',
      name: 'login',
      component: () => import('./views/LoginView.vue'),
      meta: { guestOnly: true, title: '登录' },
    },
    {
      path: '/register',
      name: 'register',
      component: () => import('./views/RegisterView.vue'),
      meta: { guestOnly: true, title: '注册' },
    },
    {
      path: '/files',
      name: 'files',
      component: () => import('./views/FilesView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '我的文件' },
    },
    {
      path: '/files/:id(\\d+)',
      name: 'folder',
      component: () => import('./views/FilesView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '我的文件' },
    },
    {
      path: '/search',
      name: 'search',
      component: () => import('./views/SearchView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '搜索' },
    },
    {
      // Shared group space (P1): the `id` is the group id, not a node id.
      path: '/groups/:id(\\d+)',
      name: 'group',
      component: () => import('./views/GroupFilesView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '组空间' },
    },
    {
      path: '/trash',
      name: 'trash',
      component: () => import('./views/TrashView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '回收站' },
    },
    {
      path: '/downloads',
      name: 'downloads',
      component: () => import('./views/DownloadView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '下载中心' },
    },
    {
      path: '/transfers',
      name: 'transfers',
      component: () => import('./views/TransfersView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '离线下载' },
    },
    {
      path: '/shares',
      name: 'shares',
      component: () => import('./views/SharesView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '我的分享' },
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('./views/SettingsView.vue'),
      meta: { requiresAuth: true, layout: 'shell', title: '设置' },
    },
    {
      path: '/admin/users',
      name: 'admin-users',
      component: () => import('./views/AdminUsersView.vue'),
      meta: { requiresAuth: true, layout: 'shell', adminOnly: true, title: '用户管理' },
    },
    {
      path: '/admin/audit',
      name: 'admin-audit',
      component: () => import('./views/AdminAuditView.vue'),
      meta: { requiresAuth: true, layout: 'shell', adminOnly: true, title: '审计日志' },
    },
    {
      path: '/admin/settings',
      name: 'admin-settings',
      component: () => import('./views/AdminSettingsView.vue'),
      meta: { requiresAuth: true, layout: 'shell', adminOnly: true, title: '系统设置' },
    },
    {
      path: '/admin/storage',
      name: 'admin-storage',
      component: () => import('./views/AdminStorageView.vue'),
      meta: { requiresAuth: true, layout: 'shell', adminOnly: true, title: '存储' },
    },
    {
      // Group console (P1): system admins see every group, delegates only theirs.
      path: '/admin/groups',
      name: 'admin-groups',
      component: () => import('./views/AdminGroupsView.vue'),
      meta: { requiresAuth: true, layout: 'shell', groupsAdmin: true, title: '组管理' },
    },
    {
      path: '/s/:token',
      name: 'share',
      component: () => import('./views/PublicShareView.vue'),
      meta: { title: '分享' },
    },
    {
      // Anonymous, one-time account-deletion cancel link (M5.2c).
      path: '/cancel-deletion',
      name: 'cancel-deletion',
      component: () => import('./views/CancelDeletionView.vue'),
      meta: { title: '账号注销' },
    },
    {
      path: '/:pathMatch(.*)*',
      name: 'not-found',
      component: () => import('./views/NotFoundView.vue'),
      meta: { title: '页面不存在' },
    },
  ],
  scrollBehavior: () => ({ top: 0 }),
})

router.beforeEach(async (to: RouteLocationNormalized) => {
  const auth = useAuthStore()

  // One /auth/me on startup; routes decide what an anonymous visitor sees.
  await auth.ensureSession()

  if (to.meta.requiresAuth && !auth.isAuthenticated) {
    const redirect = to.fullPath === '/files' ? {} : { redirect: to.fullPath }
    return { name: 'login', query: redirect }
  }

  if (to.meta.guestOnly && auth.isAuthenticated) {
    return { name: 'files' }
  }

  if (to.meta.adminOnly && !auth.isAdmin) {
    return { name: 'files' }
  }

  if (to.meta.groupsAdmin && !auth.canManageGroups) {
    return { name: 'files' }
  }

  return true
})

router.afterEach((to) => {
  const site = useSiteStore()
  site.setPageTitle(to.meta.title ?? '')
})
