<script setup>
import { computed, onMounted, ref } from 'vue'
import {
  deleteDocument,
  downloadUrl,
  listDocuments,
  replaceDocument,
  uploadDocument,
  viewUrl,
} from '@/api/documents'

// Must match the API's "Projects" setting (appsettings.json).
const projects = ['demo', 'crm']

const project = ref(projects[0])
const filterFolder = ref('')
const documents = ref([])
const loading = ref(false)
const busyId = ref(null)
const error = ref('')
const notice = ref('')

const uploadFolder = ref('')
const uploadFile = ref(null)
const uploadInput = ref(null)
const uploading = ref(false)

const replaceInput = ref(null)
const replaceTarget = ref(null)

const totalSize = computed(() => documents.value.reduce((sum, d) => sum + d.length, 0))

function flash(message) {
  notice.value = message
  error.value = ''
  setTimeout(() => {
    if (notice.value === message) notice.value = ''
  }, 4000)
}

async function run(action) {
  error.value = ''
  try {
    await action()
  } catch (e) {
    error.value = e.message
  }
}

async function load() {
  loading.value = true
  await run(async () => {
    documents.value = await listDocuments(project.value, filterFolder.value.trim())
  })
  loading.value = false
}

async function upload() {
  if (!uploadFile.value) return
  uploading.value = true
  await run(async () => {
    const doc = await uploadDocument(project.value, uploadFolder.value.trim(), uploadFile.value)
    uploadFile.value = null
    uploadInput.value.value = ''
    flash(`Uploaded ${doc.fileName}.`)
    await load()
  })
  uploading.value = false
}

function startReplace(doc) {
  replaceTarget.value = doc
  replaceInput.value.value = ''
  replaceInput.value.click()
}

async function onReplaceSelected(event) {
  const file = event.target.files?.[0]
  const doc = replaceTarget.value
  if (!file || !doc) return
  busyId.value = doc.id
  await run(async () => {
    const updated = await replaceDocument(project.value, doc.id, file)
    flash(`Replaced ${doc.fileName} with ${updated.fileName}.`)
    await load()
  })
  busyId.value = null
  replaceTarget.value = null
}

async function remove(doc) {
  if (!confirm(`Delete "${doc.fileName}"? This cannot be undone.`)) return
  busyId.value = doc.id
  await run(async () => {
    await deleteDocument(project.value, doc.id)
    flash(`Deleted ${doc.fileName}.`)
    await load()
  })
  busyId.value = null
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(1)} ${units[unit]}`
}

const formatDate = (value) => new Date(value).toLocaleString()

onMounted(load)
</script>

<template>
  <main class="page">
    <header>
      <h1>Documents</h1>
      <div class="toolbar">
        <label>
          Project
          <select v-model="project" @change="load">
            <option v-for="p in projects" :key="p" :value="p">{{ p }}</option>
          </select>
        </label>
        <label>
          Folder
          <input v-model="filterFolder" placeholder="all" @keyup.enter="load" />
        </label>
        <button type="button" :disabled="loading" @click="load">
          {{ loading ? 'Loading…' : 'Refresh' }}
        </button>
      </div>
    </header>

    <form class="card upload" @submit.prevent="upload">
      <h2>Add document</h2>
      <input ref="uploadInput" type="file" required @change="uploadFile = $event.target.files?.[0] ?? null" />
      <input v-model="uploadFolder" placeholder="Folder (optional, e.g. invoices/2026)" />
      <button type="submit" :disabled="!uploadFile || uploading">
        {{ uploading ? 'Uploading…' : 'Upload' }}
      </button>
    </form>

    <p v-if="error" class="message error" role="alert">{{ error }}</p>
    <p v-if="notice" class="message notice">{{ notice }}</p>

    <section class="card">
      <table v-if="documents.length">
        <thead>
          <tr>
            <th>File</th>
            <th>Folder</th>
            <th class="num">Size</th>
            <th>Modified</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="doc in documents" :key="doc.id" :class="{ busy: busyId === doc.id }">
            <td>
              <a :href="viewUrl(project, doc.id)" target="_blank" rel="noopener">{{ doc.fileName }}</a>
            </td>
            <td class="muted">{{ doc.folder ?? '—' }}</td>
            <td class="num">{{ formatSize(doc.length) }}</td>
            <td class="muted">{{ formatDate(doc.modified) }}</td>
            <td class="actions">
              <a :href="downloadUrl(project, doc.id)">Download</a>
              <button type="button" :disabled="busyId !== null" @click="startReplace(doc)">Replace</button>
              <button type="button" class="danger" :disabled="busyId !== null" @click="remove(doc)">Delete</button>
            </td>
          </tr>
        </tbody>
        <tfoot>
          <tr>
            <td colspan="2">{{ documents.length }} document(s)</td>
            <td class="num">{{ formatSize(totalSize) }}</td>
            <td colspan="2"></td>
          </tr>
        </tfoot>
      </table>
      <p v-else-if="!loading" class="muted empty">No documents.</p>
    </section>

    <input ref="replaceInput" type="file" hidden @change="onReplaceSelected" />
  </main>
</template>

<style scoped>
.page {
  max-width: 1000px;
  margin: 0 auto;
  padding: 24px 16px;
  font-family: system-ui, sans-serif;
  color: #1f2328;
}
header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
h1 {
  margin: 0;
}
h2 {
  margin: 0;
  font-size: 1rem;
}
.toolbar,
.upload {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
label {
  display: flex;
  align-items: center;
  gap: 6px;
}
.card {
  margin-top: 16px;
  padding: 16px;
  border: 1px solid #d0d7de;
  border-radius: 8px;
  overflow-x: auto;
}
input,
select,
button {
  font: inherit;
  padding: 4px 8px;
}
button {
  cursor: pointer;
}
button:disabled {
  cursor: default;
}
.danger {
  color: #cf222e;
}
table {
  width: 100%;
  border-collapse: collapse;
}
th,
td {
  padding: 8px;
  text-align: left;
  border-bottom: 1px solid #eaeef2;
  white-space: nowrap;
}
tfoot td {
  border-bottom: none;
  font-weight: 600;
}
.num {
  text-align: right;
}
.muted {
  color: #656d76;
}
.actions {
  display: flex;
  gap: 8px;
  align-items: center;
  justify-content: flex-end;
}
.busy {
  opacity: 0.5;
}
.empty {
  margin: 0;
}
.message {
  margin: 16px 0 0;
  padding: 8px 12px;
  border-radius: 6px;
}
.error {
  background: #ffebe9;
  color: #cf222e;
}
.notice {
  background: #dafbe1;
  color: #1a7f37;
}
</style>
