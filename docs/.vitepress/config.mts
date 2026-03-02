import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'pgroll.NET',
  description: 'Zero-downtime PostgreSQL schema migrations for .NET',

  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: '/logo.svg' }],
  ],

  themeConfig: {
    logo: { light: '/logo.svg', dark: '/logo.svg', alt: 'pgroll' },
    siteTitle: 'pgroll.NET',

    nav: [
      { text: 'Guide', link: '/getting-started' },
      { text: 'CLI', link: '/cli-reference' },
      { text: 'Operations', link: '/operations' },
      {
        text: 'Integrations',
        items: [
          { text: 'EF Core', link: '/efcore' },
          { text: 'CD Pipeline', link: '/cd-integration' },
        ],
      },
    ],

    sidebar: [
      {
        text: 'Introduction',
        items: [
          { text: 'What is pgroll?', link: '/' },
          { text: 'Getting Started', link: '/getting-started' },
        ],
      },
      {
        text: 'Reference',
        items: [
          { text: 'Migration Format', link: '/migration-format' },
          { text: 'Operations', link: '/operations' },
          { text: 'CLI Reference', link: '/cli-reference' },
        ],
      },
      {
        text: 'Integrations',
        items: [
          { text: 'EF Core', link: '/efcore' },
          { text: 'CD Pipeline', link: '/cd-integration' },
        ],
      },
      {
        text: 'Internals',
        items: [
          { text: 'Architecture', link: '/architecture' },
        ],
      },
    ],

    socialLinks: [
      { icon: 'github', link: 'https://github.com/GiuseppePatane/PgRollNet' },
    ],

    footer: {
      message: 'Port of <a href="https://github.com/xataio/pgroll" target="_blank">pgroll</a> for .NET',
      copyright: 'MIT License',
    },

    search: {
      provider: 'local',
    },

    editLink: {
      pattern: 'https://github.com/GiuseppePatane/PgRollNet/edit/master/docs/:path',
      text: 'Edit this page on GitHub',
    },
  },

  markdown: {
    theme: {
      light: 'github-light',
      dark: 'github-dark',
    },
  },
})
