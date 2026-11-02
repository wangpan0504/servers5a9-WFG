//go:build ignore

package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"longheng.io/server/internal/platform/pvf"
)

// 只接受生成器列出的差量；原 token 保留整数值与字符串引用，新字符串才追加池尾。
type token struct {
	Kind     byte
	Value    int32
	Text     *string
	Unicode  bool
	Original bool
}
type entry struct {
	Path       string  `json:"path"`
	RawFile    string  `json:"raw_file"`
	Expected   string  `json:"expected_before_sha256"`
	Type       int     `json:"data_type"`
	Tokens     []token `json:"tokens"`
	Append     bool    `json:"append"`
	CopiedFrom string  `json:"copied_from"`
	CopyHash   string  `json:"expected_copy_sha256"`
}
type skillDependency struct {
	Path      string
	RawSha256 string
}
type dependency struct {
	Source     string
	SourceHash string `json:"source_raw_sha256"`
	Skills     []skillDependency
}
type equipmentDependency struct {
	Path string `json:"equipment_path"`
	Hash string `json:"raw_sha256"`
}
type clientFile struct {
	Path string `json:"path"`
	Hash string `json:"sha256"`
}
type clientCapability struct {
	ID          string   `json:"id"`
	Implemented bool     `json:"implemented"`
	Evidence    string   `json:"evidence"`
	Files       []string `json:"files"`
}
type clientContract struct {
	Schema       string             `json:"schema"`
	Files        []clientFile       `json:"files"`
	Capabilities []clientCapability `json:"capabilities"`
}
type plan struct {
	Schema       string  `json:"schema"`
	Ready        bool    `json:"release_ready"`
	Entries      []entry `json:"entries"`
	Replacements []entry `json:"replacements"`
	Requires     bool    `json:"requires_paired_aic_ai_key_and_client"`
	Pending      []string
	Dependencies []dependency
	Equipment    []equipmentDependency `json:"equipment_dependencies"`
	Client       clientContract        `json:"client_contract"`
}

func must(err error) {
	if err != nil {
		panic(err)
	}
}
func hash(raw []byte) string { h := sha256.Sum256(raw); return hex.EncodeToString(h[:]) }
func main() {
	if len(os.Args) != 4 {
		panic("usage: PackDelta source.pvf manifest.json candidate.pvf")
	}
	source, manifest, target := os.Args[1], os.Args[2], os.Args[3]
	if strings.EqualFold(filepath.Clean(source), filepath.Clean(target)) {
		panic("source and candidate must differ")
	}
	if _, err := os.Stat(target); !os.IsNotExist(err) {
		panic("candidate must not exist")
	}
	raw, err := os.ReadFile(manifest)
	must(err)
	var p plan
	must(json.Unmarshal(raw, &p))
	if p.Schema != "support-apc-resource-delta-v1" || !p.Ready || !p.Requires || len(p.Pending) != 0 {
		panic("incomplete paired resource plan; refusing candidate")
	}
	if p.Client.Schema != "support-apc-client-contract-v1" || len(p.Client.Files) == 0 {
		panic("missing paired client contract")
	}
	clientFiles := map[string]bool{}
	for _, f := range p.Client.Files {
		key := strings.ToLower(filepath.Clean(f.Path))
		if !filepath.IsAbs(f.Path) || clientFiles[key] {
			panic("invalid or duplicate client evidence path")
		}
		raw, err := os.ReadFile(f.Path)
		must(err)
		if !strings.EqualFold(hash(raw), f.Hash) {
			panic("paired client evidence changed: " + f.Path)
		}
		clientFiles[key] = true
	}
	for _, name := range []string{"SupportBuffAi.h", "SupportBuffAiExpected.h", "SupportBuffQuery.h", "SupportApcIdentity.h", "SupportCompanion.cpp", "SupportCompanion.dll"} {
		count := 0
		for path := range clientFiles {
			if strings.EqualFold(filepath.Base(path), name) {
				count++
			}
		}
		if count != 1 {
			panic("missing or duplicate client source/binary pairing: " + name)
		}
	}
	for _, required := range []string{"recipient-selection", "effective-buff-state", "maintained-actions", "enchantress-puppet-availability", "revival-native-limits", "favoritism-stable-recipient", "player-escort-and-offense-qualification"} {
		count := 0
		for _, c := range p.Client.Capabilities {
			if c.ID == required {
				count++
				if !c.Implemented || strings.TrimSpace(c.Evidence) == "" || len(c.Files) == 0 {
					panic("incomplete client capability: " + required)
				}
				for _, path := range c.Files {
					if !clientFiles[strings.ToLower(filepath.Clean(path))] {
						panic("unpaired client evidence reference")
					}
				}
			}
		}
		if count != 1 {
			panic("missing or duplicate client capability: " + required)
		}
	}
	a, err := pvf.OpenLocalInspection(source)
	must(err)
	if len(p.Dependencies) != 4 {
		panic("missing four-template dependency evidence")
	}
	for _, d := range p.Dependencies {
		raw, err := a.ReadRaw(d.Source)
		must(err)
		if hash(raw) != d.SourceHash {
			panic("copied template source changed: " + d.Source)
		}
		for _, s := range d.Skills {
			if strings.Contains(s.Path, "#[skill]:") {
				continue
			}
			raw, err := a.ReadRaw(s.Path)
			must(err)
			if hash(raw) != s.RawSha256 {
				panic("skill dependency changed: " + s.Path)
			}
		}
	}
	for _, e := range p.Equipment {
		raw, err := a.ReadRaw(e.Path)
		must(err)
		if hash(raw) != e.Hash {
			panic("equipment dependency changed: " + e.Path)
		}
	}
	var replacements []pvf.RawReplacement
	var appends []pvf.RawAppend
	expected := map[string][]byte{}
	types := map[string]int{}
	for _, e := range append(p.Entries, p.Replacements...) {
		normalized := strings.ToLower(strings.ReplaceAll(e.Path, "\\", "/"))
		parentSegment := false
		for _, segment := range strings.Split(e.Path, "/") {
			if segment == ".." {
				parentSegment = true
				break
			}
		}
		if normalized != e.Path || parentSegment || strings.HasPrefix(e.Path, "/") {
			panic("noncanonical delta path")
		}
		if _, ok := expected[e.Path]; ok {
			panic("duplicate delta path: " + e.Path)
		}
		if e.CopiedFrom != "" {
			raw, err := a.ReadRaw(e.CopiedFrom)
			must(err)
			if hash(raw) != e.CopyHash {
				panic("copied resource changed: " + e.CopiedFrom)
			}
		}
		idx := a.FindFileIndex(e.Path)
		if e.Append {
			if idx >= 0 || e.Expected != "" {
				panic("append target already exists: " + e.Path)
			}
		} else {
			if idx < 0 || e.Expected == "" {
				panic("missing replacement or source guard: " + e.Path)
			}
			old, err := a.ReadRaw(e.Path)
			must(err)
			if hash(old) != e.Expected {
				panic("source resource changed: " + e.Path)
			}
			if a.Files()[idx].DataType != e.Type {
				panic("source data type changed: " + e.Path)
			}
		}
		var body []byte
		if e.RawFile != "" {
			if len(e.Tokens) != 0 {
				panic("both raw and tokens: " + e.Path)
			}
			body, err = os.ReadFile(e.RawFile)
			must(err)
		} else {
			if e.Type != 1 || len(e.Tokens) == 0 {
				panic("missing token script: " + e.Path)
			}
			for _, t := range e.Tokens {
				value := t.Value
				if t.Text != nil {
					if t.Kind != 3 && t.Kind != 6 {
						panic("text token type invalid")
					}
					if t.Original {
						if a.ResolveString(int(value)) != *t.Text {
							panic("original string reference changed: " + e.Path)
						}
					} else {
						var off int
						if t.Unicode {
							off, err = a.EnsureUnicodeString(*t.Text)
						} else {
							off, err = a.EnsureANSIString(*t.Text)
						}
						must(err)
						value = int32(off)
					}
				}
				var b [5]byte
				b[0] = t.Kind
				binary.LittleEndian.PutUint32(b[1:], uint32(value))
				body = append(body, b[:]...)
			}
		}
		if len(body) == 0 {
			panic("empty resource: " + e.Path)
		}
		expected[e.Path] = body
		types[e.Path] = e.Type
		if e.Append {
			appends = append(appends, pvf.RawAppend{Path: e.Path, Data: body, DataType: e.Type})
		} else {
			replacements = append(replacements, pvf.RawReplacement{Path: e.Path, Data: body})
		}
	}
	if len(expected) == 0 {
		panic("empty delta")
	}
	required := map[int]string{20900: "aicharacter/priest/heal_soldier/heal_soldier.aic", 9986: "aicharacter/support/guangbing/guangbing.aic", 74990: "aicharacter/support/femalecrusader/femalecrusader.aic", 19973: "aicharacter/support/enchantress/enchantress.aic"}
	for _, path := range required {
		if _, ok := expected[path]; !ok {
			panic("missing support template: " + path)
		}
		dir := path[:strings.LastIndex(path, "/")]
		for _, name := range []string{"event.ai", "destinationselect.ai", "action.ai", "movemethod.ai"} {
			if _, ok := expected[dir+"/ai/"+name]; !ok {
				panic("missing executable AI decision: " + dir + "/ai/" + name)
			}
		}
	}
	registry, ok := expected["aicharacter/aicharacter.lst"]
	if !ok || len(registry)%10 != 0 {
		panic("missing complete APC registry")
	}
	found := map[int]int{}
	for at := 0; at < len(registry); at += 10 {
		if registry[at] != 0 || registry[at+5] != 6 {
			panic("invalid APC registry token layout")
		}
		id := int(int32(binary.LittleEndian.Uint32(registry[at+1:])))
		if want, ok := required[id]; ok {
			value := int(int32(binary.LittleEndian.Uint32(registry[at+6:])))
			got := strings.ToLower(strings.ReplaceAll("aicharacter/"+a.ResolveString(value), "\\", "/"))
			if got != want {
				panic("support registry points to wrong template")
			}
			found[id]++
		}
	}
	for id := range required {
		if found[id] != 1 {
			panic("missing or duplicate support registry ID")
		}
	}
	// 所有输入、依赖和源保护均通过后才开始重包，失败不发布目标。
	output, err := a.RepackRawAndAppend(replacements, appends)
	must(err)
	partial := target + ".partial"
	f, err := os.OpenFile(partial, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	must(err)
	_, err = f.Write(output)
	closeErr := f.Close()
	must(err)
	must(closeErr)
	b, err := pvf.OpenLocalInspection(partial)
	must(err)
	if len(b.Files()) != len(a.Files())+len(appends) {
		panic("candidate file count changed")
	}
	for path, want := range expected {
		got, err := b.ReadRaw(path)
		must(err)
		if !bytes.Equal(got, want) {
			panic("candidate resource mismatch: " + path)
		}
		if b.Files()[b.FindFileIndex(path)].DataType != types[path] {
			panic("candidate data type mismatch: " + path)
		}
	}
	unchanged := 0
	for _, file := range a.Files() {
		path := strings.ToLower(strings.ReplaceAll(file.ArchivePath, "\\", "/"))
		if _, ok := expected[path]; ok {
			continue
		}
		old, err := a.ReadRaw(path)
		must(err)
		got, err := b.ReadRaw(path)
		must(err)
		if !bytes.Equal(old, got) || file.DataType != b.Files()[b.FindFileIndex(path)].DataType {
			panic("unrelated resource changed: " + path)
		}
		unchanged++
	}
	must(os.Rename(partial, target))
	fmt.Printf("PACK resources=%d appends=%d unchanged=%d sourceReadOnly=true wholePvfHash=false candidate=%s\n", len(expected), len(appends), unchanged, target)
}
