import {readFile} from 'node:fs/promises';
import {VirtualMachine,inspect} from '../browser/vm.mjs';
const program=JSON.parse(await readFile(process.argv[2],'utf8'));
const vm=new VirtualMachine({builtins:new Map()});console.log(inspect(await vm.execute(program)));
