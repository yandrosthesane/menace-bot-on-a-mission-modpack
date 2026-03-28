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
    // Sort by slug (which has numeric prefix) to maintain order after mapFn strips it
    if ((!a.isFolder && !b.isFolder) || (a.isFolder && b.isFolder)) {
      const slugA = a.slug ?? a.displayName ?? ""
      const slugB = b.slug ?? b.displayName ?? ""
      return slugA.localeCompare(slugB, undefined, { numeric: true, sensitivity: "base" })
    }
    return a.isFolder ? -1 : 1
  },
  mapFn: (node: any) => {
    // Rename folder display names to hide nesting
    if (node.isFolder) {
      const name = node.displayName?.toLowerCase()
      if (name === "docs" || name === "features") {
        node.displayName = ""
      }
    }
    if (!node.isFolder) {
      let name = node.displayName ?? ""
      // Strip numeric prefix (e.g. "01_README_INSTALL" → "README_INSTALL")
      name = name.replace(/^\d+_/, "")
      // Strip README_ prefix and title-case
      if (name.startsWith("README_")) {
        name = name.substring(7).replace(/_/g, " ").toLowerCase().replace(/\b\w/g, (c: string) => c.toUpperCase())
      }
      node.displayName = name
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
