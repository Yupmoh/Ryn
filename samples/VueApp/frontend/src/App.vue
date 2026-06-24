<script setup lang="ts">
import { ref } from 'vue'
import SystemPanel from './components/SystemPanel.vue'
import TodoPanel from './components/TodoPanel.vue'
import IpcPlayground from './components/IpcPlayground.vue'

const tabs = ['System', 'Todos', 'IPC'] as const
type Tab = typeof tabs[number]
const activeTab = ref<Tab>('System')

</script>

<template>
  <div class="app">
    <!-- data-webview-drag makes this strip a window-drag handle natively (saucer hit-tests it and starts the
         OS drag inside the mousedown — zero IPC, no desync). It works on every backend, unlike the CSS
         -webkit-app-region, which only Chromium/WebView2 honors. See docs/custom-title-bars.md. -->
    <header data-webview-drag>
      <div class="logo">
        <span class="accent">Ryn</span> + Vue
      </div>
      <!-- data-webview-ignore keeps the buttons clickable instead of dragging the window. -->
      <nav data-webview-ignore>
        <button
          v-for="tab in tabs"
          :key="tab"
          :class="{ active: activeTab === tab }"
          @click="activeTab = tab"
        >
          {{ tab }}
        </button>
      </nav>
    </header>
    <main>
      <SystemPanel v-if="activeTab === 'System'" />
      <TodoPanel v-else-if="activeTab === 'Todos'" />
      <IpcPlayground v-else />
    </main>
  </div>
</template>

<style>
* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
  background: #0a0a0f;
  color: #e0e0e0;
}

.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
}

header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  height: 56px;
  background: #111118;
  border-bottom: 1px solid #1e1e2e;
}

.logo {
  font-size: 1.3em;
  font-weight: 700;
  letter-spacing: -0.5px;
}

.accent {
  color: #7c3aed;
}

nav {
  display: flex;
  gap: 4px;
}

nav button {
  padding: 8px 18px;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: #888;
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s;
}

nav button:hover {
  color: #ccc;
  background: #1a1a2a;
}

nav button.active {
  color: #e0e0e0;
  background: #7c3aed22;
  color: #a78bfa;
}

main {
  flex: 1;
  overflow: auto;
  padding: 24px;
}
</style>
