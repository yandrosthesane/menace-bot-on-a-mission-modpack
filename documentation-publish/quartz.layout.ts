import { PageLayout, SharedLayout } from "./quartz/cfg"
import * as Component from "./quartz/components"

// components shared across all pages
export const sharedPageComponents: SharedLayout = {
  head: Component.Head(),
  header: [],
  afterBody: [],
  footer: Component.Footer({
    links: {
      GitHub: "https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack",
    },
  }),
}

// Explorer: flatten docs/features/ so pages appear at root level
const explorerConfig = {
  filterFn: (node: any) => {
    // Hide the intermediate "docs" and "features" folder nodes
    if (node.isFolder) {
      const name = node.displayName?.toLowerCase()
      if (name === "docs" || name === "features") return true // keep folder but it gets flattened
    }
    return true
  },
  sortFn: (a: any, b: any) => {
    // Sort by frontmatter "order" field if present, then alphabetically
    const orderA = a.file?.frontmatter?.order ?? 999
    const orderB = b.file?.frontmatter?.order ?? 999
    if (orderA !== orderB) return orderA - orderB
    // Folders before files
    if (a.isFolder && !b.isFolder) return -1
    if (!a.isFolder && b.isFolder) return 1
    return (a.displayName ?? "").localeCompare(b.displayName ?? "")
  },
  mapFn: (node: any) => {
    // Rename folder display names to hide nesting
    if (node.isFolder) {
      const name = node.displayName?.toLowerCase()
      if (name === "docs" || name === "features") {
        node.displayName = ""
      }
    }
    // Clean up README_ prefix from page names and title-case
    if (!node.isFolder && node.displayName?.startsWith("README_")) {
      node.displayName = node.displayName
        .substring(7)
        .replace(/_/g, " ")
        .toLowerCase()
        .replace(/\b\w/g, (c: string) => c.toUpperCase())
    }
  },
}

// components for pages that display a single page (e.g. a single note)
export const defaultContentPageLayout: PageLayout = {
  beforeBody: [
    Component.ConditionalRender({
      component: Component.Breadcrumbs(),
      condition: (page) => page.fileData.slug !== "index",
    }),
    Component.ArticleTitle(),
    Component.ContentMeta(),
    Component.TagList(),
  ],
  left: [
    Component.PageTitle(),
    Component.MobileOnly(Component.Spacer()),
    Component.Flex({
      components: [
        {
          Component: Component.Search(),
          grow: true,
        },
        { Component: Component.Darkmode() },
        { Component: Component.ReaderMode() },
      ],
    }),
    Component.Explorer(explorerConfig),
  ],
  right: [
    Component.DesktopOnly(Component.TableOfContents()),
  ],
}

// components for pages that display lists of pages  (e.g. tags or folders)
export const defaultListPageLayout: PageLayout = {
  beforeBody: [Component.Breadcrumbs(), Component.ArticleTitle(), Component.ContentMeta()],
  left: [
    Component.PageTitle(),
    Component.MobileOnly(Component.Spacer()),
    Component.Flex({
      components: [
        {
          Component: Component.Search(),
          grow: true,
        },
        { Component: Component.Darkmode() },
      ],
    }),
    Component.Explorer(explorerConfig),
  ],
  right: [],
}
