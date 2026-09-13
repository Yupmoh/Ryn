#!/usr/bin/env node

const { run } = require('vue-tsc');

// vue-tsc 3.x requires the classic TypeScript compiler API removed in TypeScript 7.
run(require.resolve('@typescript/typescript6/lib/tsc'));
